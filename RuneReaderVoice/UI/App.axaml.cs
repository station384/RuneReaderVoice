// SPDX-License-Identifier: GPL-3.0-only
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton
//
// RuneReaderVoice is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, version 3 of the License.
//
// RuneReaderVoice is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with RuneReaderVoice. If not, see <https://www.gnu.org/licenses/>.

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RuneReaderVoice.UI.Views;

namespace RuneReaderVoice;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            desktop.Exit += OnExit;
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        // Save settings on clean exit
        VoiceSettingsManager.SaveSettings(AppServices.Settings);

        // Shut down background services first so they cannot keep the process alive.
        try { RuneReaderVoice.AppServices.NpcSync.Dispose(); } catch { }
        try { RuneReaderVoice.AppServices.Monitor.Dispose(); } catch { }

        // Dispose all services
        try { RuneReaderVoice.AppServices.Coordinator.Dispose(); } catch { }
        try { RuneReaderVoice.AppServices.Cache.Dispose(); } catch { }
        try { RuneReaderVoice.AppServices.Provider.Dispose(); } catch { }
        try { RuneReaderVoice.AppServices.Player.Dispose(); } catch { }
        try { RuneReaderVoice.AppServices.Platform.Dispose(); } catch { }
        try { RuneReaderVoice.AppServices.Db.Dispose(); } catch { }
    }
}