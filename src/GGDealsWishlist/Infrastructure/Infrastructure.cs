using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Input;

namespace GGDealsWishlist.Infrastructure
{
    public abstract class ObservableBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool SetValue<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>Signals that every bound property may have changed.</summary>
        protected void OnAllPropertiesChanged() => OnPropertyChanged(string.Empty);
    }

    public class RelayCommand : ICommand
    {
        private readonly Action<object> execute;
        private readonly Func<object, bool> canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
            : this(_ => execute(), canExecute == null ? (Func<object, bool>)null : _ => canExecute())
        {
        }

        public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
        {
            this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
            this.canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object parameter) => canExecute?.Invoke(parameter) ?? true;

        public void Execute(object parameter)
        {
            try
            {
                execute(parameter);
            }
            catch (Exception e)
            {
                Log.Error(e, "Command failed");
            }
        }
    }

    /// <summary>Async command that ignores re-entry while running and never lets exceptions escape.</summary>
    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<object, Task> execute;
        private readonly Func<object, bool> canExecute;
        private bool isRunning;

        public AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute = null)
            : this(_ => execute(), canExecute == null ? (Func<object, bool>)null : _ => canExecute())
        {
        }

        public AsyncRelayCommand(Func<object, Task> execute, Func<object, bool> canExecute = null)
        {
            this.execute = execute;
            this.canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object parameter) => !isRunning && (canExecute?.Invoke(parameter) ?? true);

        public async void Execute(object parameter)
        {
            if (isRunning)
            {
                return;
            }

            isRunning = true;
            CommandManager.InvalidateRequerySuggested();
            try
            {
                await execute(parameter);
            }
            catch (Exception e)
            {
                Log.Error(e, "Async command failed");
            }
            finally
            {
                isRunning = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>ObservableCollection that can swap its whole content with a single Reset notification.</summary>
    public sealed class BulkObservableCollection<T> : ObservableCollection<T>
    {
        public void ReplaceAll(IEnumerable<T> items)
        {
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(item);
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    /// <summary>
    /// Thin wrapper over the framework's JavaScriptSerializer. It is used instead of Newtonsoft.Json so the
    /// extension never conflicts with the Newtonsoft version that Playnite itself loads.
    /// </summary>
    public static class Json
    {
        private static JavaScriptSerializer Create() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };

        public static string Serialize(object value) => Create().Serialize(value);

        public static T Deserialize<T>(string json) => Create().Deserialize<T>(json);

        /// <summary>Parses into loose Dictionary / object[] / primitive values.</summary>
        public static object Parse(string json) => Create().DeserializeObject(json);

        public static T ConvertTo<T>(object loose) => Create().ConvertToType<T>(loose);

        public static IDictionary<string, object> AsObject(object value) => value as IDictionary<string, object>;

        public static object Get(IDictionary<string, object> obj, string name)
        {
            if (obj == null)
            {
                return null;
            }

            return obj.TryGetValue(name, out var value) ? value : null;
        }

        /// <summary>Reads a value that may be a JSON string or number as text; null for anything else.</summary>
        public static string GetText(IDictionary<string, object> obj, string name)
        {
            var value = Get(obj, name);
            switch (value)
            {
                case string s:
                    return s;
                case int i:
                    return i.ToString(CultureInfo.InvariantCulture);
                case long l:
                    return l.ToString(CultureInfo.InvariantCulture);
                case decimal d:
                    return d.ToString(CultureInfo.InvariantCulture);
                case double db:
                    return db.ToString(CultureInfo.InvariantCulture);
                default:
                    return null;
            }
        }

        public static bool? GetBool(IDictionary<string, object> obj, string name) => Get(obj, name) is bool b ? b : (bool?)null;

        public static int? GetInt(IDictionary<string, object> obj, string name)
        {
            var text = GetText(obj, name);
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : (int?)null;
        }
    }

    /// <summary>
    /// Encrypts secrets with Windows DPAPI (current user scope) so the API key is never stored in plain text
    /// in the Playnite settings file and cannot be decrypted by another Windows account.
    /// </summary>
    public static class SecretProtector
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GGDealsWishlist.Playnite.ApiKey.v1");

        public static string Protect(string secret)
        {
            if (string.IsNullOrEmpty(secret))
            {
                return null;
            }

            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }

        /// <summary>Returns null when nothing is stored or the blob cannot be decrypted (e.g. copied from another PC).</summary>
        public static string Unprotect(string protectedSecret)
        {
            if (string.IsNullOrEmpty(protectedSecret))
            {
                return null;
            }

            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception e) when (e is CryptographicException || e is FormatException)
            {
                Log.Warn("Stored API key could not be decrypted; it must be entered again.");
                return null;
            }
        }
    }

    public interface IClock
    {
        DateTime UtcNow { get; }
    }

    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();

        public DateTime UtcNow => DateTime.UtcNow;
    }
}
