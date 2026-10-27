// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.GnmMode;
using SharpEmu.GnmMode.Libraries.Gnm;
using SharpEmu.GnmMode.Shaders;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Agc;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

[Collection(AgcCommandBufferChainCollection.Name)]
public sealed class GnmComputeExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong Ring = Base + 0x1000;
    private const ulong ReadPointer = Base + 0x100;
    private const ulong Result = Base + 0x200;

    [Fact]
    public void RingCommandsExecuteOnTheSharedAgcComputeQueue()
    {
        var context = Context();
        var id = Map(context);
        Write(context, Ring, PacketHeader.Make(5, PacketOpcode.WriteData), 0x100, unchecked((uint)Result), (uint)(Result >> 32), 123);
        Assert.Equal(0, Ding(context, id, 5));
        Assert.True(context.TryReadUInt32(Result, out var result));
        Assert.Equal(123u, result);
        Assert.True(context.TryReadUInt32(ReadPointer, out var read));
        Assert.Equal(5u, read);
        Assert.Equal(1UL, AgcExports.GetHeadlessCommandStreamForTests(context.Memory).Queue.SubmissionsStarted);
        Assert.Equal(0, Ding(context, id, 5));
        Assert.Equal(1UL, AgcExports.GetHeadlessCommandStreamForTests(context.Memory).Queue.SubmissionsStarted);
    }

    [Fact]
    public void ReadPointerDoesNotAdvanceUntilBlockedCommandsAreConsumed()
    {
        var context = Context();
        var id = Map(context);
        Write(context, Ring, PacketHeader.Make(7, PacketOpcode.WaitRegisterMemory), 0x13,
            unchecked((uint)Result), (uint)(Result >> 32), 123, uint.MaxValue, 10);
        Assert.Equal(0, Ding(context, id, 7));
        Assert.True(context.TryReadUInt32(ReadPointer, out var read));
        Assert.Equal(0u, read);
        var stream = AgcExports.GetHeadlessCommandStreamForTests(context.Memory);
        Assert.Equal(1, stream.Queue.BlockedQueueCount);
        Assert.Equal(0, Ding(context, id, 7));
        Assert.True(context.TryWriteUInt32(Result, 123));
        stream.RetryBlocked();
        Assert.True(context.TryReadUInt32(ReadPointer, out read));
        Assert.Equal(7u, read);
        Assert.False(stream.Queue.HasPending);
        Assert.Equal(1UL, stream.Queue.SubmissionsStarted);
    }

    [Fact]
    public void RingWrapSubmitsTailThenHead()
    {
        var context = Context();
        var id = Map(context, dwords: 16);
        Write(context, Ring, PacketHeader.Make(12, PacketOpcode.Nop));
        Assert.Equal(0, Ding(context, id, 12));
        Write(context, Ring + 12 * 4, PacketHeader.Make(4, PacketOpcode.Nop));
        Write(context, Ring, PacketHeader.Make(5, PacketOpcode.WriteData), 0x100, unchecked((uint)Result), (uint)(Result >> 32), 456,
            PacketHeader.Make(3, PacketOpcode.Nop), 0, 0);
        Assert.Equal(0, Ding(context, id, 8));
        Assert.True(context.TryReadUInt32(Result, out var result));
        Assert.Equal(456u, result);
        Assert.True(context.TryReadUInt32(ReadPointer, out var read));
        Assert.Equal(8u, read);
        Assert.Equal(3UL, AgcExports.GetHeadlessCommandStreamForTests(context.Memory).Queue.SubmissionsStarted);
    }

    [Fact]
    public void GcnReleaseMemoryTranslatesToAgcWithoutChangingPs5Packets()
    {
        var context = Context();
        var id = Map(context);
        Write(context, Ring, PacketHeader.Make(7, PacketOpcode.ReleaseMemory) | 2,
            0x528, 1u << 29, unchecked((uint)Result), (uint)(Result >> 32), 789, 0);
        Assert.Equal(0, Ding(context, id, 7));
        Assert.True(context.TryReadUInt32(Result, out var result));
        Assert.Equal(789u, result);
        Assert.True(context.TryReadUInt32(ReadPointer, out var read));
        Assert.Equal(7u, read);
    }

    [Fact]
    public void ComputeShaderPacketUsesSharedShaderRegisters()
    {
        var context = Context();
        var id = Map(context);
        Write(context, Base + 0x800, 0x1234, 0, 0x5678, 0x9ABC, 64, 1, 1);
        context[CpuRegister.Rdi] = Ring;
        context[CpuRegister.Rsi] = 25;
        context[CpuRegister.Rdx] = Base + 0x800;
        Assert.Equal(0, GnmDriverExports.SetCsShader(context));
        Assert.Equal(0, Ding(context, id, 25));
        var registers = AgcExports.GetHeadlessCommandStreamForTests(context.Memory).Queue.GetInterpreter(1).Registers.Shader;
        Assert.Equal(0x1234u, registers[ShaderRegisterOffset.ComputePgmLo]);
        Assert.Equal(0x5678u, registers[ShaderRegisterOffset.ComputePgmRsrc1]);
        Assert.Equal(64u, registers[ShaderRegisterOffset.ComputeNumThreadX]);
    }

    [Fact]
    public void ComputeDepthLoadsResolveTheKnownGpuPlaneInsteadOfAZeroBufferProjection()
    {
        var context = Context();
        var id = Map(context);
        Write(context, Ring, PacketHeader.Make(10, PacketOpcode.SetContextRegister), 0x10,
            3, 0, 0x00400000, 0, 0x00400000, 0, 31u | (15u << 11), 511);
        Assert.Equal(0, Ding(context, id, 10));
        const ulong codeAddress = Base + 0x4000;
        Write(context, codeAddress, 0xF0000100, 0x00020000, 0xBF810000);
        Assert.True(GcnShaderDecoder.TryDecode(context.Memory, codeAddress, 12, out var raw, out var error), error);
        Assert.True(GcnStorageImageLowering.TryLower(raw, out var lowered, out error), error);
        var frontend = new GcnShaderFrontend();
        var plan = ShaderResourcePlan.Extract(lowered, ShaderStage.Compute, 54321, 0, 16,
            normalizeBufferDescriptor: frontend.NormalizeBufferDescriptor,
            normalizeImageDescriptor: frontend.NormalizeImageDescriptor);
        uint[] descriptor = [0x00400000, 0x1C400000, 255u | (127u << 14), 0x90000204, 255u << 13, 0, 0, 0];
        var user = new uint[16];
        descriptor.CopyTo(user, 8);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, new ResourceRuntimeInputs { UserData = user },
            ref snapshot, ref specialization, out var failure), failure.ToString());
        var native = new TextureDescriptorWords(Assert.Single(snapshot.Images));
        Assert.Equal(0x40000000UL, native.BaseAddress);
        Assert.Equal(GuestTileMode.Depth, native.TileMode);
        Assert.All(snapshot.Buffers, words => Assert.Equal(0UL, BufferDescriptorWords.From(words).Address));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(lowered, 0, 16),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout),
            out _, out error), error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultHardwareStateIsAnExecutableAgcReset(bool compute)
    {
        var context = Context();
        context[CpuRegister.Rdi] = Ring;
        context[CpuRegister.Rsi] = 256;
        Assert.Equal(256, compute ? GnmDriverExports.DispatchInitDefaultHardwareState(context)
            : GnmDriverExports.DrawInitDefaultHardwareState200(context));
        Assert.Equal(0, AgcExports.SubmitComputeCommands(context, 0x20, Ring, 256));
        Assert.False(AgcExports.GetHeadlessCommandStreamForTests(context.Memory).Queue.HasPending);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    [InlineData(7u)]
    public void GcnColorPitchAndSliceProduceATwoDimensionalAgcTarget(uint slot)
    {
        var context = Context();
        var id = Map(context);
        var offset = 0x318u + slot * 15;
        Write(context, Ring, PacketHeader.Make(17, PacketOpcode.SetContextRegister), offset,
            0x00400000, 239, 32639, 0, 10u << 2, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(0, Ding(context, id, 17));
        var target = AgcExports.GetHeadlessCommandStreamForTests(context.Memory)
            .Queue.GetInterpreter(1).TypedRegisters.Context.ColorTargets[slot];
        Assert.Equal(0x40000000UL, target.BaseAddress);
        Assert.Equal(1920u, target.Width + 1);
        Assert.Equal(1088u, target.Height + 1);
        Assert.Equal(1u, target.Dimension);
        Assert.Equal(GuestTileMode.RenderTarget, target.TileMode);
        var resolved = ImageRequestBuilders.ColorTarget(in target, 15, 0, false);
        Assert.NotNull(resolved);
        Assert.Equal(1920u, resolved.Value.Extent.Width);
        Assert.Equal(1088u, resolved.Value.Extent.Height);

        Write(context, Ring + 17 * 4, PacketHeader.Make(3, PacketOpcode.SetContextRegister), offset + 2, 16319);
        Assert.Equal(0, Ding(context, id, 20));
        var updated = AgcExports.GetHeadlessCommandStreamForTests(context.Memory)
            .Queue.GetInterpreter(1).TypedRegisters.Context.ColorTargets[slot];
        Assert.Equal(544u, updated.Height + 1);
        Assert.Equal(1088u, target.Height + 1);
    }

    [Fact]
    public void SampledColorTargetKeepsItsGpuBackingAtAGcnOnlyAlignedAddress()
    {
        var context = Context();
        var id = Map(context);
        Write(context, Ring, PacketHeader.Make(17, PacketOpcode.SetContextRegister), 0x318,
            0x00408080, 239, 32639, 0, 10u << 2, 14, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(0, Ding(context, id, 17));
        var target = AgcExports.GetHeadlessCommandStreamForTests(context.Memory)
            .Queue.GetInterpreter(1).TypedRegisters.Context.ColorTargets[0];
        var frontend = new GcnShaderFrontend();
        uint[] gcn = [0x00408080, 10u << 20, 1919u | (1079u << 14),
            (9u << 28) | (14u << 20) | 0xFAC, 1919u << 13, 0, 0, 0];
        var converted = frontend.NormalizeImageDescriptor(gcn, SharpEmu.ShaderCompiler.Resources.ShaderStage.Pixel, 123, "test-gpu-target");
        var shape = new ShaderImageShape(false, false, false, false, false, TextureNumericClass.Float);
        var sampled = frontend.ResolveTexture(converted, shape, 123, 0);
        var color = ImageRequestBuilders.ColorTarget(in target, 15, 0, false)!.Value;
        Assert.Equal(target.BaseAddress, sampled.Request.Description.Data.Address);
        Assert.Equal(GuestTileMode.RenderTarget, sampled.Request.Description.TileMode);
        Assert.Equal(color.Request.Description.Pitch, sampled.Request.Description.Pitch);
        var colorMip = color.Request.Description.MipLayout[0];
        var textureMip = sampled.Request.Description.MipLayout[0];
        Assert.True(colorMip.Size == textureMip.Size && colorMip.Pitch == textureMip.Pitch,
            $"color={colorMip.Size:X}/{colorMip.Pitch}/{colorMip.Height} texture={textureMip.Size:X}/{textureMip.Pitch}/{textureMip.Height}");
        Assert.Equal(1920u, sampled.Request.Description.Extent.Width);
        Assert.Equal(target.Height + 1, sampled.Request.Description.Extent.Height);
        Assert.Equal(color.Request.Description.Data, sampled.Request.Description.Data);
        Assert.Equal(0x00408080u, gcn[0]);
    }

    [Fact]
    public void LegacyColorExtentWritesInvalidateCachedTargetsAndPreserveSnapshots()
    {
        var context = Context();
        var id = Map(context);
        Write(context, Ring, PacketHeader.Make(17, PacketOpcode.SetContextRegister), 0x318,
            0x00400000, 239, 32639, 0, 10u << 2, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(0, Ding(context, id, 17));
        var banks = AgcExports.GetHeadlessCommandStreamForTests(context.Memory).Queue.GetInterpreter(1).TypedRegisters;
        var snapshot = banks.Snapshot();
        var stamp = banks.Stamp(RegisterStamp.ColorTargets);

        Write(context, Ring + 17 * 4, PacketHeader.Make(3, PacketOpcode.SetContextRegister), 0x31A, 16319);
        Assert.Equal(0, Ding(context, id, 20));

        Assert.True(banks.Stamp(RegisterStamp.ColorTargets) > stamp);
        Assert.Equal(544u, banks.Context.ColorTargets[0].Height + 1);
        Assert.Equal(1088u, snapshot.Context.ColorTargets[0].Height + 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullGcnSurfaceCopyUsesNativeImagesRatherThanTheSmallerLegacyBufferRange(bool threadDimensions)
    {
        var context = Context();
        var id = Map(context);
        const ulong source = 0x0040808000;
        const ulong destination = 0x0041800000;
        Write(context, Ring, PacketHeader.Make(17, PacketOpcode.SetContextRegister), 0x318,
            (uint)(source >> 8), 239, 32639, 0, 0x28828, 14, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(0, Ding(context, id, 17));
        const ulong codeAddress = Base + 0x4000;
        uint[] code = [0xBEEB03FF, 7, 0x8F00860C, 0x4A000000, 0xE0002000, 0x80010100,
            0x7D88000A, 0xBE80246A, 0xBF8C0F70, 0xE0102000, 0x80020100, 0xBF810000];
        Write(context, codeAddress, code);
        var shader = new RegisteredShader(codeAddress, 0, 48, 0, 0, 0, 0, 0, 0);
        var frontend = new GcnShaderFrontend();
        uint[] user = [0, 0, 0, 0, (uint)source, 0x40000, 2088960, 0x24004,
            (uint)destination, 0x40000, 2088960, 0x24004];
        var input = new ComputeInputInfo { ThreadsX = 64, ThreadsY = 1, ThreadsZ = 1,
            DispatchThreadDimensions = threadDimensions };
        var system = new Gen5ComputeSystemRegisters(12, null, null, null);
        var x = threadDimensions ? 2088960u : 32640u;
        Assert.True(frontend.TryResolveImageCopy(context, shader, user, input, system, x, 1, 1, out var from, out var to));
        Assert.Equal(source, from.Description.Data.Address);
        Assert.Equal(destination, to.Description.Data.Address);
        Assert.True(from.Description.Data.Size > 2088960UL * 4);
        Assert.Equal(from.Description.Data.Size, to.Description.Data.Size);
        Assert.Equal(from.Description.Extent, to.Description.Extent);

        // Partial copies, modified kernels and mismatched descriptor lengths stay on the normal path.
        Assert.False(frontend.TryResolveImageCopy(context, shader, user, input, system, x - 1, 1, 1, out _, out _));
        user[10]--;
        Assert.False(frontend.TryResolveImageCopy(context, shader, user, input, system, x, 1, 1, out _, out _));
        user[10]++;
        code[6] = 0x7D88000B;
        Write(context, codeAddress, code);
        Assert.False(frontend.TryResolveImageCopy(context, shader, user, input, system, x, 1, 1, out _, out _));
    }

    [Theory]
    [InlineData(7u, 0u, 0u, 0x80D17000u)]
    [InlineData(0u, 8u, 0u, 0x80D17001u)]
    [InlineData(0u, 0u, 3u, 0x80D17002u)]
    public void InvalidQueueArgumentsReturnGnmErrors(uint pipe, uint queue, uint dwords, uint error)
    {
        var context = Context();
        SetMapArguments(context, dwords);
        context[CpuRegister.Rdi] = pipe;
        context[CpuRegister.Rsi] = queue;
        Assert.Equal(unchecked((int)error), GnmDriverExports.MapComputeQueue(context));
    }

    [Fact]
    public void QueueMappingsAndDeveloperServicesAreGnmOnly()
    {
        foreach (var nid in new[] { "29oKvKXzEZo", "bX5IbRvECXk", "iCO804ZgzdA", "KXltnCwEJHQ" })
        {
            Assert.Single(GnmExportRegistry.CreateExports(), export => export.Nid == nid);
            Assert.DoesNotContain(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5), export => export.Nid == nid);
        }
        Assert.Equal(unchecked((int)0x80D13FFF), GnmDriverExports.ValidateCommandBuffers(Context()));
    }

    private static CpuContext Context() => new(new FakeCpuMemory(Base, 0x10000), Generation.Gen4);

    private static void SetMapArguments(CpuContext context, uint dwords)
    {
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = Ring;
        context[CpuRegister.Rcx] = dwords;
        context[CpuRegister.R8] = ReadPointer;
    }

    private static uint Map(CpuContext context, uint dwords = 256)
    {
        SetMapArguments(context, dwords);
        Assert.Equal(1, GnmDriverExports.MapComputeQueue(context));
        return (uint)context[CpuRegister.Rax];
    }

    private static int Ding(CpuContext context, uint id, uint offset)
    {
        context[CpuRegister.Rdi] = id;
        context[CpuRegister.Rsi] = offset;
        return GnmDriverExports.DingDong(context);
    }

    private static void Write(CpuContext context, ulong address, params uint[] values)
    {
        var bytes = new byte[values.Length * sizeof(uint)];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * sizeof(uint)), values[i]);
        Assert.True(context.Memory.TryWrite(address, bytes));
    }
}
