// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

// A legacy binary frontend produces the same IR without interpreting its bytes as AGC.
public interface IShaderFrontend
{
    ulong CacheDomain { get; }
    uint VertexUserDataBase => 8;
    bool HasVertexTables => true;
    bool RequiresExplicitBufferLengths => false;
    BufferDescriptorNormalizer? NormalizeBufferDescriptor => null;
    ImageDescriptorNormalizer? NormalizeImageDescriptor => null;
    Func<long>? ImageNormalizationVersion => null;
    TextureRequestResolution ResolveTexture(ReadOnlySpan<uint> words, in ShaderImageShape shape,
        ulong shaderHash, int imageIndex) => ImageRequestBuilders.Texture(words, shape, shaderHash, imageIndex);
    void PrepareTexture(ReadOnlySpan<uint> words) { }
    bool TryResolveImageCopy(CpuContext context, RegisteredShader shader, ReadOnlySpan<uint> userData,
        ComputeInputInfo input, Gen5ComputeSystemRegisters systemRegisters, uint x, uint y, uint z,
        out ImageRequest source, out ImageRequest destination)
    {
        source = destination = default;
        return false;
    }
    RegisteredShader ReadShader(CpuContext context, ulong codeAddress, string label, ReadOnlySpan<uint> userData = default);
    RegisteredShader? ReadMergedHull(CpuContext context, ulong localAddress, ulong hullAddress, ReadOnlySpan<uint> userData) => null;
    (ulong Address, ulong Bytes) TessellationLocalDataRing => default;
    bool TryDecode(CpuContext context, RegisteredShader shader, out Gen5ShaderProgram program, out string error);
    bool TryDecode(CpuContext context, RegisteredShader shader, ShaderStage stage, out Gen5ShaderProgram program, out string error) =>
        TryDecode(context, shader, out program, out error);
}
