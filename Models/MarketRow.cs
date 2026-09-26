using System.ComponentModel;

namespace TrenchHQ.Models
{
    internal sealed class MarketRow(string symbol, string venueId, string venueName) : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string Symbol { get; } = symbol;
        public string VenueId { get; } = venueId;
        public string VenueName { get; } = venueName;
        public string TagValue => $"{Symbol} - {VenueName}";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
