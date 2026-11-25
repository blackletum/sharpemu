// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

public sealed partial class GuestImageCache
{
    private const ulong ExhaustedReclaimFrames = 30;
    private const ulong PressureReclaimFrames = 120;
    private const ulong PressureReclaimInterval = 30;
    private const ulong BudgetQueryInterval = 30;
    private const long PressureFreeBytes = 512 * (long)MiB;
    private const long MigrationHeadroomBytes = 1024 * (long)MiB;
    private const ulong MigrationBytesPerFrame = 128 * MiB;
    private const int ReclaimScanLimit = 4096;
    private const int ReclaimDeletionLimit = 256;
    private const ulong MigrationDelayFrames = 4;
    private const ulong MigrationRetryFrames = 30;
    private const int MaxDeviceMemoryReports = 24;

    private enum MigrationOutcome
    {
        Done,
        Progress,
        Waiting,
        Blocked,
    }

    private readonly HashSet<ResourceSlotIdentifier> _systemMemoryImages = new();
    private long _observedExhaustions;
    private bool _reportedExhaustion;
    private ulong _nextMigrationTick;
    private ulong _nextPressureReclaimTick;
    private int _deviceMemoryReports;
    private ulong _migratedImages;
    private ulong _migratedBytes;
    private ulong _nextBudgetQueryTick;
    private bool _haveBudget;
    private ulong _budgetUsage;
    private ulong _budget;
    private ulong _migratedSinceBudget;

    public int SystemMemoryImageCount => _systemMemoryImages.Count;

    private void ManageDeviceMemory(ulong tick)
    {
        var exhaustions = _device.DeviceLocalExhaustions;
        if (exhaustions != _observedExhaustions)
        {
            _observedExhaustions = exhaustions;
            RefreshBudget(tick, force: true);
            if (!_reportedExhaustion)
            {
                _reportedExhaustion = true;
                ReportImageMemory("device-local-exhausted", includeLargest: true);
            }

            ReclaimDeviceMemory(tick, ExhaustedReclaimFrames, "exhausted", releaseCaches: true);
            _nextMigrationTick = tick + MigrationDelayFrames;
            _nextPressureReclaimTick = tick + PressureReclaimInterval;
            return;
        }

        RefreshBudget(tick, force: false);
        var free = FreeDeviceMemory();
        if (_haveBudget && free < PressureFreeBytes && tick >= _nextPressureReclaimTick)
        {
            ReclaimDeviceMemory(tick, PressureReclaimFrames, "near-budget", releaseCaches: false);
            _nextPressureReclaimTick = tick + PressureReclaimInterval;
        }

        if (_systemMemoryImages.Count == 0 || tick < _nextMigrationTick)
        {
            return;
        }

        var migratedBefore = _migratedBytes;
        var outcome = MigrateSystemMemoryImages(free);
        _migratedSinceBudget += _migratedBytes - migratedBefore;
        switch (outcome)
        {
            case MigrationOutcome.Progress:
                _nextMigrationTick = tick + 1;
                break;
            case MigrationOutcome.Waiting:
            case MigrationOutcome.Blocked:
                _nextMigrationTick = tick + MigrationRetryFrames;
                break;
            default:
                if (_migratedImages != 0)
                {
                    ReportDeviceMemory($"Images that were placed in system memory are back in device-local memory: moved={_migratedImages} bytes={_migratedBytes}" +
                        DescribeDeviceMemory());
                }

                break;
        }
    }

    private void RefreshBudget(ulong tick, bool force)
    {
        if (!force && tick < _nextBudgetQueryTick)
        {
            return;
        }

        _haveBudget = _device.TryGetDeviceLocalBudget(out _budgetUsage, out _budget);
        _nextBudgetQueryTick = tick + BudgetQueryInterval;
        _migratedSinceBudget = 0;
    }

    private long FreeDeviceMemory() =>
        _haveBudget ? (long)_budget - (long)_budgetUsage - (long)_migratedSinceBudget : long.MaxValue;

    private string DescribeDeviceMemory() =>
        (_haveBudget ? $" vram_usage_mib={_budgetUsage / MiB} vram_budget_mib={_budget / MiB}" : string.Empty) +
        $" image_bytes_mib={_totalUsedMemory / MiB} buffer_bytes_mib={_bufferCache.TotalUsedMemory / MiB}" +
        $" system_memory_images={_systemMemoryImages.Count} system_memory_buffers={_device.SystemMemoryBuffers}" +
        $" system_memory_buffer_mib={_device.SystemMemoryBufferBytes / MiB}";

    private bool ReclaimDeviceMemory(ulong tick, ulong ageFrames, string reason, bool releaseCaches)
    {
        var parked = 0;
        var pooled = 0UL;
        if (releaseCaches)
        {
            for (var index = _parked.Count - 1; index >= 0; index--)
            {
                var imageIdentifier = _parked[index];
                _parked.RemoveAt(index);
                var image = _slots.TryGet(imageIdentifier);
                if (image is { Registered: true, Parked: true })
                {
                    image.Parked = false;
                    ReleaseImage(imageIdentifier);
                    parked++;
                }
            }

            pooled = _backingPool?.PooledBytes ?? 0;
            _backingPool?.Trim();
        }

        var deleted = 0;
        var deletedBytes = 0UL;
        var canRecord = _scheduler.Active && !_scheduler.Current.IsInvalid;
        if (tick > ageFrames)
        {
            var candidates = new List<ResourceSlotIdentifier>();
            _recencyQueue.ForEachItemAtOrBeforeTick(tick - ageFrames, imageIdentifier =>
            {
                candidates.Add(imageIdentifier);
                return candidates.Count >= ReclaimScanLimit;
            });
            foreach (var imageIdentifier in candidates)
            {
                if (deleted >= ReclaimDeletionLimit)
                {
                    break;
                }

                var bytes = _slots.TryGet(imageIdentifier)?.Backing.AllocationSize ?? 0;
                if (TryReclaimImage(imageIdentifier, canRecord))
                {
                    deleted++;
                    deletedBytes += bytes;
                }
            }
        }

        var released = parked > 0 || deleted > 0 || pooled > 0;
        if (released)
        {
            ReportDeviceMemory($"Device memory {reason}: released parked={parked} stale={deleted} stale_mib={deletedBytes / MiB} pooled_mib={pooled / MiB}" +
                DescribeDeviceMemory());
        }

        return released;
    }

    private bool TryReclaimImage(ResourceSlotIdentifier imageIdentifier, bool canRecord)
    {
        var image = _slots.TryGet(imageIdentifier);
        if (image == null || !image.Registered || image.DepthOwner.IsValid)
        {
            return false;
        }

        if (image.IsGpuModified)
        {
            if (image.IsMaybeCpuDirty)
            {
                if (image.NeedsMaybeCpuHash)
                {
                    return false;
                }

                _ = image.ResolveMaybeCpuHash(image.HashGuestEdges());
            }

            var safe = CanReadBack(image);
            if (!safe && image.SafeToDownload)
            {
                return false;
            }

            if (safe && !image.Description.IsTiled && (!canRecord || !TryDownloadToGuest(imageIdentifier)))
            {
                return false;
            }

            ReportImageLifetime(image, "reclaim-device-memory", deletion: true);
            image.ClearGpuModified();
        }
        else
        {
            ReportImageLifetime(image, "reclaim-device-memory-clean", deletion: true);
        }

        DeleteImage(imageIdentifier);
        return true;
    }

    private MigrationOutcome MigrateSystemMemoryImages(long free)
    {
        if (!_scheduler.Active || _scheduler.Current.IsInvalid)
        {
            return MigrationOutcome.Progress;
        }

        var moved = 0UL;
        var waiting = false;
        foreach (var imageIdentifier in _systemMemoryImages.ToArray())
        {
            var image = _slots.TryGet(imageIdentifier);
            if (image is not { Registered: true } || !image.Backing.InSystemMemory || !CanMoveBacking(image))
            {
                _systemMemoryImages.Remove(imageIdentifier);
                continue;
            }

            if (image.Parked)
            {
                waiting = true;
                continue;
            }

            var size = image.Backing.AllocationSize;
            if (moved != 0 && moved + size > MigrationBytesPerFrame)
            {
                return MigrationOutcome.Progress;
            }

            if (free != long.MaxValue && free - (long)size < MigrationHeadroomBytes)
            {
                waiting = true;
                continue;
            }

            var replacement = new CachedImage(_device, _scheduler, _backing, image.Description, _backingPool, deviceLocalOnly: true);
            if (!replacement.Backing.Exists)
            {
                replacement.Dispose();
                return MigrationOutcome.Blocked;
            }

            replacement.CopyFrom(image);
            image.ExchangeBacking(replacement);
            if (image.Binding.IsBound || image.Binding.IsTarget)
            {
                image.Binding.NeedsRebind = true;
            }

            InvalidateLookups(image.Description.Data.Address, image.Description.Data.End);
            RetireAfterCurrentTick(replacement);
            _systemMemoryImages.Remove(imageIdentifier);
            moved += size;
            if (free != long.MaxValue)
            {
                free -= (long)size;
            }

            _migratedImages++;
            _migratedBytes += size;
        }

        return _systemMemoryImages.Count == 0 ? MigrationOutcome.Done
            : waiting && moved == 0 ? MigrationOutcome.Waiting
            : MigrationOutcome.Progress;
    }

    private static bool CanMoveBacking(CachedImage image) =>
        image.Backing.Exists && (ViewFormatRules.FullAspects(image.Backing.Format) & ImageAspectFlags.StencilBit) == 0;

    private void RetireAfterCurrentTick(CachedImage retired)
    {
        if (_scheduler.Active)
        {
            _scheduler.QueueCompletionAction(retired.Dispose);
        }
        else
        {
            retired.Dispose();
        }
    }

    private void ReportDeviceMemory(string message)
    {
        if (_deviceMemoryReports < MaxDeviceMemoryReports)
        {
            _deviceMemoryReports++;
            Console.Error.WriteLine($"[GPU][INFO] {message}");
        }
    }
}
