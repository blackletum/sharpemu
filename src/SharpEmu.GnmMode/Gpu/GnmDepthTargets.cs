// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.GnmMode.Libraries.VideoOut;
using SharpEmu.GnmMode.Shaders;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.GnmMode.Gpu;

internal static class GnmDepthTargets
{
    private const uint DepthSetupFirst = 0x010;
    private const uint DepthSetupCount = 8;
    private const uint DepthSlice = 0x017;
    private static readonly ConditionalWeakTable<GpuCommandInterpreter, GuestPlanes> Guests = new();
    private static readonly ConcurrentDictionary<(ulong Guest, Plane Plane), Shadow> Shadows = new();
    private static ICpuMemory? _memory;

    private enum Plane { Depth, Stencil, Htile }

    private readonly record struct Shadow(ulong Address, ulong Size);

    private sealed class GuestPlanes
    {
        public ulong ZRead;
        public ulong ZWrite;
        public ulong StencilRead;
        public ulong StencilWrite;
        public ulong Htile;
        public uint SliceTiles;
    }

    internal static void Reset(ICpuMemory memory)
    {
        _memory = memory;
        Shadows.Clear();
    }

    internal static bool Touches(uint start, uint count) =>
        Overlaps(start, count, ContextRegisterOffset.DbDepthView, 1) ||
        Overlaps(start, count, ContextRegisterOffset.DbHtileDataBase, 1) ||
        Overlaps(start, count, DepthSetupFirst, DepthSetupCount);

    internal static void Observe(GpuCommandInterpreter interpreter, in PacketContext packet, uint start, ReadOnlySpan<uint> values)
    {
        var guest = Guests.GetOrCreateValue(interpreter);
        for (var index = 0; index < values.Length; index++)
        {
            var address = (ulong)values[index] << 8;
            switch (start + (uint)index)
            {
                case ContextRegisterOffset.DbZReadBase: guest.ZRead = address; break;
                case ContextRegisterOffset.DbZWriteBase: guest.ZWrite = address; break;
                case ContextRegisterOffset.DbStencilReadBase: guest.StencilRead = address; break;
                case ContextRegisterOffset.DbStencilWriteBase: guest.StencilWrite = address; break;
                case ContextRegisterOffset.DbHtileDataBase: guest.Htile = address; break;
                case DepthSlice: guest.SliceTiles = (values[index] & 0x3FFFFF) + 1; break;
            }
        }
        Translate(interpreter, in packet);
    }

    internal static void Translate(GpuCommandInterpreter interpreter, in PacketContext packet)
    {
        var guest = Guests.GetOrCreateValue(interpreter);
        var depth = interpreter.TypedRegisters.Context.DepthTarget;
        var zRead = Guest(guest.ZRead, depth.ZReadBase);
        var zWrite = Guest(guest.ZWrite, depth.ZWriteBase);
        var hasStencil = depth.StencilFormat != GuestStencilFormat.Invalid;
        var stencilRead = hasStencil ? Guest(guest.StencilRead, depth.StencilReadBase) : 0;
        var stencilWrite = hasStencil ? Guest(guest.StencilWrite, depth.StencilWriteBase) : 0;
        var htile = Guest(guest.Htile, depth.HtileBase);
        var (hostZRead, hostZWrite, hostStencilRead, hostStencilWrite, hostHtile) = (zRead, zWrite, stencilRead, stencilWrite, htile);

        var width = depth.XMax + 1;
        var height = depth.YMax + 1;
        var layers = depth.SliceMax + 1;
        var slicePixels = (ulong)guest.SliceTiles * 64 * layers << (int)depth.SamplesLog2;
        var allocatedDepth = slicePixels * (depth.DepthFormat == GuestDepthFormat.Z16 ? 2UL : 4UL);
        if (depth.DepthSizeValid &&
            TileGeometry.TryGetDepthSize(width, height, 0, depth.DepthFormat, depth.StencilFormat, depth.HtileAcceleration,
                out var stencilSize, out var htileSize, out var depthSize, depth.SamplesLog2))
        {
            hostZRead = Place(zRead, Plane.Depth, (ulong)depthSize.Size * layers, depthSize.Align, allocatedDepth);
            hostZWrite = zWrite == zRead ? hostZRead : Place(zWrite, Plane.Depth, (ulong)depthSize.Size * layers, depthSize.Align, allocatedDepth);
            if (hasStencil)
            {
                hostStencilRead = Place(stencilRead, Plane.Stencil, (ulong)stencilSize.Size * layers, stencilSize.Align, slicePixels);
                hostStencilWrite = stencilWrite == stencilRead ? hostStencilRead
                    : Place(stencilWrite, Plane.Stencil, (ulong)stencilSize.Size * layers, stencilSize.Align, slicePixels);
            }
            if (depth.HtileAcceleration)
                hostHtile = Place(htile, Plane.Htile, (ulong)htileSize.Size * layers, htileSize.Align, 0);
            if (zRead != 0 && DepthFormatRule.Find(depth.DepthFormat) is { } rule)
                GcnImageDescriptor.ObserveDepthTarget(zRead, hostZRead, width, height, layers, rule.GuestFormat);
        }

        Write(interpreter, in packet, ContextRegisterOffset.DbZReadBase, ContextRegisterOffset.DbZReadBaseHi, depth.ZReadBase, hostZRead);
        Write(interpreter, in packet, ContextRegisterOffset.DbZWriteBase, ContextRegisterOffset.DbZWriteBaseHi, depth.ZWriteBase, hostZWrite);
        Write(interpreter, in packet, ContextRegisterOffset.DbStencilReadBase, ContextRegisterOffset.DbStencilReadBaseHi, depth.StencilReadBase, hostStencilRead);
        Write(interpreter, in packet, ContextRegisterOffset.DbStencilWriteBase, ContextRegisterOffset.DbStencilWriteBaseHi, depth.StencilWriteBase, hostStencilWrite);
        Write(interpreter, in packet, ContextRegisterOffset.DbHtileDataBase, ContextRegisterOffset.DbHtileDataBaseHi, depth.HtileBase, hostHtile);
    }

    private static ulong Guest(ulong observed, ulong current) => observed != 0 ? observed : current;

    private static ulong Place(ulong guest, Plane plane, ulong size, uint align, ulong allocated)
    {
        var fits = allocated == 0 || size <= allocated;
        if (guest == 0 || size == 0 || align == 0 || fits && (guest & (align - 1UL)) == 0)
            return guest;
        if (Shadows.TryGetValue((guest, plane), out var shadow) && shadow.Size >= size)
            return shadow.Address;
        if (_memory is not { } memory ||
            !GnmVideoOutExports.TryAllocateTrackedShadow(new CpuContext(memory, Generation.Gen4), size, out var address))
        {
            Console.Error.WriteLine($"[GNMMODE][ERROR] Depth {plane} shadow allocation failed: guest=0x{guest:X16} size=0x{size:X} align=0x{align:X}.");
            return guest;
        }
        Shadows[(guest, plane)] = new Shadow(address, size);
        if (GnmDiagnostics.Log)
            Console.Error.WriteLine($"[GNMMODE][TRACE] Depth {plane} plane guest=0x{guest:X16} host=0x{address:X16} size=0x{size:X} align=0x{align:X} gcnSize=0x{allocated:X}.");
        return address;
    }

    private static void Write(GpuCommandInterpreter interpreter, in PacketContext packet, uint low, uint high, ulong current, ulong address)
    {
        if (current == address)
            return;
        var normalized = packet with { Header = PacketHeader.Make(3, PacketOpcode.SetContextRegister) };
        var handler = PacketDispatchTable.Opcodes[PacketOpcode.SetContextRegister]!;
        Span<uint> fields = [low, (uint)(address >> 8)];
        _ = handler(interpreter, in normalized, fields);
        fields[0] = high;
        fields[1] = (uint)(address >> 40);
        _ = handler(interpreter, in normalized, fields);
    }

    private static bool Overlaps(uint start, uint count, uint first, uint length) =>
        start < first + length && first < start + count;
}
