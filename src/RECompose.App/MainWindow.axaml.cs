// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;

namespace RECompose.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        EngineStatus.Text = "Starting the audio engine…";
    }

    public void SetEngineStatus(string text) => EngineStatus.Text = text;
}
