// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Pipelines;
using System.Runtime.CompilerServices;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    private static readonly ConditionalWeakTable<ICpuMemory, IShaderFrontend> _shaderFrontends = new();

    public static void SetShaderFrontend(ICpuMemory memory, IShaderFrontend frontend)
    {
        while (memory is ICpuMemoryWrapper wrapper) memory = wrapper.Inner;
        _shaderFrontends.AddOrUpdate(memory, frontend);
    }

    private static IShaderFrontend? ShaderFrontendOf(ICpuMemory memory)
    {
        while (memory is ICpuMemoryWrapper wrapper) memory = wrapper.Inner;
        return _shaderFrontends.TryGetValue(memory, out var frontend) ? frontend : null;
    }
    // This partial exposes the shader headers the create export registered.

    internal static ulong GetShaderHeaderAddress(ulong codeAddress) =>
        _shaderHeadersByCode.TryGetValue(codeAddress, out var header) ? header : 0;

    internal static ShaderHeaderRegistry CreateShaderHeaderRegistry(CpuContext context) =>
        new(
            context,
            GetShaderHeaderAddress,
            codeAddress => ShaderCompiler.Gen5ShaderTranslator.TryGetFusedProgramParts(context, codeAddress, out var continuation, out var continuationHeader)
                ? new FusedProgramParts(continuation, continuationHeader)
                : null,
            ShaderFrontendOf(context.Memory));
}
