// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Numerics;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.GnmMode.Shaders;

public sealed record GcnStorageImageControl(uint Address, uint Data, uint Resource, bool Array, uint Dmask) : Gen5InstructionControl;
