using System;
using Avalonia.Controls;
using VkMusik.Services;
using VkMusik.ViewModels;

namespace VkMusik.Views;

public partial class LoginWindow : Window
{
    private readonly LoginViewModel _viewModel = new();

    public LoginWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.LoggedIn += session => LoggedIn?.Invoke(session);

        Opened += async (_, _) => await _viewModel.InitializeAsync();
        Closed += (_, _) => _viewModel.Dispose();
    }

    /// <summary>Вход выполнен — приложение может открывать главное окно.</summary>
    public event Action<SavedSession>? LoggedIn;
}
