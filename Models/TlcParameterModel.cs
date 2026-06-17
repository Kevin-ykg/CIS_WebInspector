using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CIS_WebInspector.Models
{
    public class TlcParameterOption
    {
        public string DisplayText { get; set; }
        public string Value { get; set; }
    }

    public class TlcParameterModel : INotifyPropertyChanged
    {
        private string _value;

        public string Name { get; set; }
        public string Description { get; set; }
        public string[] MatchKeys { get; set; }
        
        public TlcParameterOption[] Options { get; set; }
        public bool HasOptions => Options != null && Options.Length > 0;
        
        public string Value
        {
            get => _value;
            set
            {
                if (_value != value)
                {
                    _value = value;
                    OnPropertyChanged();
                }
            }
        }

        public Func<string, bool> ApplyAction { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
