using System;
using Microsoft.UI.Xaml;
namespace ExplorerTabUtility.IntegrationTests;
public partial class TestApp : Application
{
    public TestApp()
    {
        UnhandledException += (_, args) => Console.Error.WriteLine(args.Exception);
        InitializeComponent();
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args) => await global::Program.RunAll();
}
