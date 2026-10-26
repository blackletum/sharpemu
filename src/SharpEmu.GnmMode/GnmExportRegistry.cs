// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.GnmMode.Libraries.Ajm;
using SharpEmu.GnmMode.Libraries.AppContent;
using SharpEmu.GnmMode.Libraries.Gnm;
using SharpEmu.GnmMode.Libraries.SaveData;
using SharpEmu.GnmMode.Libraries.VideoOut;

namespace SharpEmu.GnmMode;

public static class GnmExportRegistry
{
    public static IReadOnlyList<ExportedFunction> CreateExports()
    {
        ExportedFunction[] overrides =
        [
            new("libSceSaveData", "32HQAQdwM2o", "sceSaveDataMount", Generation.Gen4,
                GnmSaveDataExports.Mount),
            new("libSceSaveData", "0z45PIH+SNI", "sceSaveDataMount2", Generation.Gen4,
                GnmSaveDataExports.Mount2),
            new("libSceAppContent", "99b82IKXpH4", "sceAppContentAppParamGetInt", Generation.Gen4,
                GnmAppContentExports.AppParamGetInt),
            new("libSceAjm", "AxoDrINp4J8", "sceAjmInstanceCreate", Generation.Gen4,
                GnmAjmExports.InstanceCreate),
            new("libSceAjm", "RbLbuKv8zho", "sceAjmInstanceDestroy", Generation.Gen4,
                GnmAjmExports.InstanceDestroy),
            new("libSceAjm", "dmDybN--Fn8", "sceAjmBatchJobControlBufferRa", Generation.Gen4,
                GnmAjmExports.BatchJobControlBufferRa),
            new("libSceAjm", "ElslOCpOIns", "sceAjmBatchJobRunBufferRa", Generation.Gen4,
                GnmAjmExports.BatchJobRunBufferRa),
            new("libSceAjm", "7jdAXK+2fMo", "sceAjmBatchJobRunSplitBufferRa", Generation.Gen4,
                GnmAjmExports.BatchJobRunSplitBufferRa),
            new("libSceAjm", "stlghnic3Jc", "sceAjmBatchJobInlineBuffer", Generation.Gen4,
                GnmAjmExports.BatchJobInlineBuffer),
            new("libSceAjm", "fFFkk0xfGWs", "sceAjmBatchStartBuffer", Generation.Gen4,
                GnmAjmExports.BatchStartBuffer),
            new("libSceVideoOut", "SbU3dwp80lQ", "sceVideoOutGetFlipStatus", Generation.Gen4,
                GnmVideoOutExports.GetFlipStatus),
            new("libSceVideoOut", "CBiu4mCE1DA", "sceVideoOutSetFlipRate", Generation.Gen4,
                GnmVideoOutExports.SetFlipRate),
            new("libSceVideoOut", "w3BY+tAEiQY", "sceVideoOutRegisterBuffers", Generation.Gen4,
                GnmVideoOutExports.RegisterBuffers),
            new("libSceGnmDriver", "gAhCn6UiU4Y", "sceGnmSetVsShader", Generation.Gen4, GnmDriverExports.SetVsShader),
            new("libSceGnmDriver", "bQVd5YzCal0", "sceGnmSetPsShader", Generation.Gen4, GnmDriverExports.SetPsShader),
            new("libSceGnmDriver", "vckdzbQ46SI", "sceGnmSetLsShader", Generation.Gen4, GnmDriverExports.SetLsShader),
            new("libSceGnmDriver", "VJNjFtqiF5w", "sceGnmSetHsShader", Generation.Gen4, GnmDriverExports.SetHsShader),
            new("libSceGnmDriver", "FUHG8sQ3R58", "sceGnmSetEsShader", Generation.Gen4, GnmDriverExports.SetEsShader),
            new("libSceGnmDriver", "UJwNuMBcUAk", "sceGnmSetGsShader", Generation.Gen4, GnmDriverExports.SetGsShader),
            new("libSceGnmDriver", "zwY0YV91TTI", "sceGnmSubmitCommandBuffers", Generation.Gen4, GnmDriverExports.SubmitCommandBuffers),
            new("libSceGnmDriver", "xbxNatawohc", "sceGnmSubmitAndFlipCommandBuffers", Generation.Gen4, GnmDriverExports.SubmitAndFlipCommandBuffers),
            new("libSceGnmDriver", "yvZ73uQUqrk", "sceGnmSubmitDone", Generation.Gen4, GnmDriverExports.SubmitDone),
            new("libSceGnmDriver", "W1Etj-jlW7Y", "sceGnmInsertPushMarker", Generation.Gen4, GnmDriverExports.InsertMarker),
            new("libSceGnmDriver", "7qZVNgEu+SY", "sceGnmInsertPopMarker", Generation.Gen4, GnmDriverExports.InsertMarker),
            new("libSceGnmDriver", "ln33zjBrfjk", "sceGnmGetTheTessellationFactorRingBufferBaseAddress", Generation.Gen4,
                GnmDriverExports.GetTessellationFactorRingBufferBaseAddress),
            new("libSceGnmDriver", "iCO804ZgzdA", "sceGnmValidateCommandBuffers", Generation.Gen4,
                GnmDriverExports.ValidateCommandBuffers),
            new("libSceGnmDriver", "29oKvKXzEZo", "sceGnmMapComputeQueue", Generation.Gen4,
                GnmDriverExports.MapComputeQueue),
            new("libSceGnmDriver", "bX5IbRvECXk", "sceGnmDingDong", Generation.Gen4, GnmDriverExports.DingDong),
            new("libSceGnmDriver", "jg33rEKLfVs", "sceGnmIsUserPaEnabled", Generation.Gen4, GnmDriverExports.IsUserPaEnabled),
            new("libSceGnmDriver", "KXltnCwEJHQ", "sceGnmSetCsShader", Generation.Gen4, GnmDriverExports.SetCsShader),
            new("libSceGnmDriver", "nF6bFRUBRAU", "sceGnmDispatchInitDefaultHardwareState", Generation.Gen4,
                GnmDriverExports.DispatchInitDefaultHardwareState),
            new("libSceGnmDriver", "0H2vBYbTLHI", "sceGnmDrawInitDefaultHardwareState200", Generation.Gen4,
                GnmDriverExports.DrawInitDefaultHardwareState200),
        ];
        var overridden = overrides.Select(export => export.Nid).ToHashSet(StringComparer.Ordinal);
        return [.. overrides, .. SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4)
            .Where(export => !overridden.Contains(export.Nid))];
    }
}
