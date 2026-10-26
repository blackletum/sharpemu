// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;

namespace SharpEmu.GnmMode.Libraries.Gnm;

public static partial class GnmDriverExports
{
    public static int IsUserPaEnabled(CpuContext context) => context.SetReturn(0);

    public static int DispatchInitDefaultHardwareState(CpuContext context) => WriteDefaultState(context, compute: true);
    public static int DrawInitDefaultHardwareState200(CpuContext context) => WriteDefaultState(context, compute: false);

    private static int WriteDefaultState(CpuContext context, bool compute)
    {
        const uint dwords = 0x100;
        if ((uint)context[CpuRegister.Rsi] < dwords || context[CpuRegister.Rdi] == 0)
            return context.SetReturn(0);
        Span<byte> commands = stackalloc byte[(int)dwords * sizeof(uint)];
        commands.Clear();
        // Use the same queue defaults as AGC instead of maintaining a second hardware state table.
        BinaryPrimitives.WriteUInt32LittleEndian(commands,
            PacketHeader.Make(2, PacketOpcode.Nop, compute ? PacketCustomCode.DispatchReset : PacketCustomCode.DrawReset));
        BinaryPrimitives.WriteUInt32LittleEndian(commands[8..], PacketHeader.Make(dwords - 2, PacketOpcode.Nop));
        return context.SetReturn(context.Memory.TryWrite(context[CpuRegister.Rdi], commands) ? (int)dwords : 0);
    }

    public static int SetCsShader(CpuContext context)
    {
        if (context[CpuRegister.Rdi] == 0 || (uint)context[CpuRegister.Rsi] < 25 || context[CpuRegister.Rdx] == 0)
            return context.SetReturn(-1);
        Span<byte> registers = stackalloc byte[7 * sizeof(uint)];
        if (!KernelMemoryCompatExports.TryReadGuestStructure(context, context[CpuRegister.Rdx], registers) ||
            BinaryPrimitives.ReadUInt32LittleEndian(registers[4..]) != 0)
            return context.SetReturn(-1);
        Span<byte> commands = stackalloc byte[25 * sizeof(uint)];
        commands.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(commands, PacketHeader.Make(4, PacketOpcode.SetShaderRegister));
        BinaryPrimitives.WriteUInt32LittleEndian(commands[4..], ShaderRegisterOffset.ComputePgmLo);
        registers[..8].CopyTo(commands[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(commands[16..], PacketHeader.Make(4, PacketOpcode.SetShaderRegister));
        BinaryPrimitives.WriteUInt32LittleEndian(commands[20..], ShaderRegisterOffset.ComputePgmRsrc1);
        registers.Slice(8, 8).CopyTo(commands[24..]);
        BinaryPrimitives.WriteUInt32LittleEndian(commands[32..], PacketHeader.Make(5, PacketOpcode.SetShaderRegister));
        BinaryPrimitives.WriteUInt32LittleEndian(commands[36..], ShaderRegisterOffset.ComputeNumThreadX);
        registers[16..].CopyTo(commands[40..]);
        BinaryPrimitives.WriteUInt32LittleEndian(commands[52..], PacketHeader.Make(12, PacketOpcode.Nop));
        return context.SetReturn(context.Memory.TryWrite(context[CpuRegister.Rdi], commands) ? 0 : -1);
    }
}
