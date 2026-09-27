using TrenchHQ.Core.Panels;
using TrenchHQ.Core.Widgets;
using TrenchHQ.Core.Windows;
using TrenchHQ.Infrastructure.Panels;
using TrenchHQ.Infrastructure.Widgets;
using TrenchHQ.Infrastructure.Windows;
using TrenchHQ.Panels;
using TrenchHQ.Presentation;
using TrenchHQ.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace TrenchHQ.ViewModels
{
    internal sealed record PanelContentOption(string Name, SavedWidgetDefinition? Widget)
    {
        public bool IsApplicationWindow => Widget == null;
    }

}
