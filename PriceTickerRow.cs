using System;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using TrenchHQ.Helpers;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace TrenchHQ
{
    public sealed class PriceTickerRow : INotifyPropertyChanged
    {
        private static readonly Color UpTickColor = Color.FromArgb(255, 0, 255, 0);
        private static readonly Color DownTickColor = Color.FromArgb(255, 255, 0, 0);
        private string _displaySymbol;
        private string _priceText;
        private string _priceToolTip;
        private string _symbol;
        private string _venueId;
        private Color _defaultPriceColor = Color.FromArgb(255, 245, 245, 245);
        private double _lastPrice;
        private bool _hasLastPrice;
        private bool _priceFlashOnChange;
        private PriceTickState _priceTickState;
        private BigInteger _lastExactCoefficient;
        private uint _lastExactScale;
        private bool _hasExactPrice;
        private double _contentScale = 1d;

        public PriceTickerRow(
            string displaySymbol,
            string priceText,
            string symbol,
            string venueId,
            string? iconUri = null)
        {
            _displaySymbol = displaySymbol;
            _priceText = priceText;
            _priceToolTip = priceText;
            _symbol = symbol;
            _venueId = venueId;
            if (Uri.TryCreate(iconUri, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps
                    || string.Equals(uri.Scheme, "ms-appx", StringComparison.OrdinalIgnoreCase)))
            {
                IconSource = new BitmapImage(uri);
                IconVisibility = Visibility.Visible;
            }
        }

        public SolidColorBrush PriceForeground { get; } = new(Color.FromArgb(255, 245, 245, 245));
        public BitmapImage? IconSource { get; }
        public Visibility IconVisibility { get; } = Visibility.Collapsed;
        public Thickness OverlayPadding => new(0, 8 * _contentScale, 0, 8 * _contentScale);
        public Thickness OverlayMargin => new(8 * _contentScale, 0, 8 * _contentScale, 0);
        public double OverlaySpacing => 8 * _contentScale;
        public double OverlayIconSize => 20 * _contentScale;
        public double OverlayFontSize => 14 * _contentScale;
        public double OverlayPriceMaxWidth => 112 * _contentScale;
        public double HorizontalMinWidth => 210 * _contentScale;
        public double HorizontalMinHeight => 35 * _contentScale;
        public Thickness HorizontalPadding => new(12 * _contentScale, 0, 12 * _contentScale, 0);
        public double HorizontalSpacing => 8 * _contentScale;
        public double HorizontalIconSize => 16 * _contentScale;
        public double HorizontalFontSize => 12 * _contentScale;
        public double VerticalMinWidth => 150 * _contentScale;
        public double VerticalMinHeight => 52 * _contentScale;
        public Thickness VerticalPadding => new(9 * _contentScale, 8 * _contentScale, 9 * _contentScale, 8 * _contentScale);
        public double VerticalSpacing => 6 * _contentScale;
        public double VerticalIconSize => 16 * _contentScale;
        public double VerticalFontSize => 12 * _contentScale;

        public string DisplaySymbol
        {
            get => _displaySymbol;
            set
            {
                if (_displaySymbol != value)
                {
                    _displaySymbol = value;
                    OnPropertyChanged();
                }
            }
        }

        public string PriceText
        {
            get => _priceText;
            set
            {
                if (_priceText != value)
                {
                    _priceText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string PriceToolTip
        {
            get => _priceToolTip;
            set
            {
                if (_priceToolTip != value)
                {
                    _priceToolTip = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Symbol
        {
            get => _symbol;
            set
            {
                if (_symbol != value)
                {
                    _symbol = value;
                    OnPropertyChanged();
                }
            }
        }

        public string VenueId
        {
            get => _venueId;
            set
            {
                if (_venueId != value)
                {
                    _venueId = value;
                    OnPropertyChanged();
                }
            }
        }

        public void SetDefaultPriceColor(Color color)
        {
            _defaultPriceColor = color;
            if (!_priceFlashOnChange || !_hasLastPrice)
            {
                ApplyPriceTickState(PriceTickState.Default);
            }
        }

        public void SetPriceFlashEnabled(bool enabled)
        {
            _priceFlashOnChange = enabled;
            if (!enabled)
            {
                ApplyPriceTickState(PriceTickState.Default);
            }
        }

        public void SetContentScale(double scale)
        {
            var normalized = PanelContentSizeRules.NormalizeScale(scale);
            if (Math.Abs(_contentScale - normalized) < 0.001)
            {
                return;
            }

            _contentScale = normalized;
            foreach (var propertyName in new[]
                     {
                         nameof(OverlayPadding), nameof(OverlayMargin), nameof(OverlaySpacing),
                         nameof(OverlayIconSize), nameof(OverlayFontSize), nameof(OverlayPriceMaxWidth),
                         nameof(HorizontalMinWidth), nameof(HorizontalMinHeight), nameof(HorizontalPadding),
                         nameof(HorizontalSpacing), nameof(HorizontalIconSize), nameof(HorizontalFontSize),
                         nameof(VerticalMinWidth), nameof(VerticalMinHeight), nameof(VerticalPadding),
                         nameof(VerticalSpacing), nameof(VerticalIconSize), nameof(VerticalFontSize)
                     })
            {
                OnPropertyChanged(propertyName);
            }
        }

        public void UpdatePrice(double value, string formatted)
        {
            PriceText = formatted;
            ApplyPriceTickState(PriceTickRules.GetNextState(
                _priceFlashOnChange,
                _hasLastPrice,
                _lastPrice,
                value,
                _priceTickState));
            _lastPrice = value;
            _hasLastPrice = true;
            _hasExactPrice = false;
        }

        public void UpdateExactPrice(string coefficient, uint scale, string formatted)
        {
            if (!BigInteger.TryParse(coefficient, out var current))
            {
                return;
            }
            PriceText = formatted;
            var nextState = PriceTickState.Default;
            if (_priceFlashOnChange && _hasExactPrice)
            {
                var comparison = CompareExact(current, scale, _lastExactCoefficient, _lastExactScale);
                nextState = comparison > 0
                    ? PriceTickState.Up
                    : comparison < 0
                        ? PriceTickState.Down
                        : _priceTickState;
            }
            ApplyPriceTickState(nextState);
            _lastExactCoefficient = current;
            _lastExactScale = scale;
            _hasExactPrice = true;
            _hasLastPrice = true;
        }

        private static int CompareExact(BigInteger left, uint leftScale, BigInteger right, uint rightScale)
        {
            if (leftScale == rightScale)
            {
                return left.CompareTo(right);
            }
            return leftScale < rightScale
                ? (left * BigInteger.Pow(10, checked((int)(rightScale - leftScale)))).CompareTo(right)
                : left.CompareTo(right * BigInteger.Pow(10, checked((int)(leftScale - rightScale))));
        }

        private void ApplyPriceTickState(PriceTickState state)
        {
            _priceTickState = state;
            PriceForeground.Color = state switch
            {
                PriceTickState.Up => UpTickColor,
                PriceTickState.Down => DownTickColor,
                _ => _defaultPriceColor
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
