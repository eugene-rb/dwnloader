using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Dwnloader;

/// <summary>
/// bool を Visibility へ。true=Visible / false=Collapsed。
///
/// WPF には組み込みの BooleanToVisibilityConverter があったが WinUI3 には無い。
/// MainWindow の進捗バー・メッセージ・各操作ボタンの表示は、すべて EntryVm の
/// bool プロパティ（ShowProgress / CanOpen など）をこのコンバータ経由で
/// Visibility に写している。反転が要る箇所は今のところ無いのでパラメータは見ない。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}
