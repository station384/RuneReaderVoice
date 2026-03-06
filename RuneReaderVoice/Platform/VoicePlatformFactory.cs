// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton
//
// RuneReaderVoice is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// RuneReaderVoice is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with RuneReaderVoice. If not, see <https://www.gnu.org/licenses/>.

// VoicePlatformFactory.cs
// Creates the platform-appropriate IVoicePlatformServices implementation.
// Same #if WINDOWS / #if LINUX pattern as RuneReader's PlatformFactory.

#if WINDOWS
using RuneReaderVoice.Platform.Windows;
#elif LINUX
using RuneReaderVoice.Platform.Linux;
#endif

namespace RuneReaderVoice.Platform;

public static class VoicePlatformFactory
{
    public static IVoicePlatformServices Create()
    {
#if WINDOWS
        return new WindowsVoicePlatformServices();
#elif LINUX
        return new LinuxVoicePlatformServices();
#else
        throw new PlatformNotSupportedException(
            "RuneReader Voice supports Windows and Linux only.");
#endif
    }
}
