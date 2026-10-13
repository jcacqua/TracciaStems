using System;
using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;

namespace TracciaStems
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // les liaisons de données utilisent la langue du système (virgule décimale en français)
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

            DispatcherUnhandledException += OnUnhandled;
            base.OnStartup(e);
        }

        private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show("Erreur inattendue :\n\n" + e.Exception.Message, "TracciaStems", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
