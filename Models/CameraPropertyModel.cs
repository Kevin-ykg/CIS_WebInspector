using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CIS_WebInspector.Models
{
    /// <summary>采集卡枚举属性的可绑定模型；Value 变化由 ViewModel 转发到底层设备。</summary>
    public class CameraPropertyModel : INotifyPropertyChanged
    {
        private uint _value;

        public string Name { get; set; }

        public uint Value
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

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
