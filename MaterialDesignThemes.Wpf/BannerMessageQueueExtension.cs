using System;
using System.Windows.Markup;

namespace MaterialDesignThemes.Wpf
{
    /// <summary>
    /// Provides shorthand to initialise a new <see cref="BannerMessageQueue"/> for a <see cref="Banner"/>.
    /// </summary>
    [MarkupExtensionReturnType(typeof(BannerMessageQueue))]    
    public class BannerMessageQueueExtension : MarkupExtension
    {        
        public override object ProvideValue(IServiceProvider serviceProvider)
        {            
            return new BannerMessageQueue();
        }
    }
}