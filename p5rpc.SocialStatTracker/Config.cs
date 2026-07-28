using p5rpc.SocialStatTracker.Template.Configuration;
using System.ComponentModel;

namespace p5rpc.SocialStatTracker.Configuration
{
    public class Config : Configurable<Config>
    {
        /*
            User Properties:
                - Please put all of your configurable properties here.

            By default, configuration saves as "Config.json" in mod user config folder.    
            Need more config files/classes? See Configuration.cs

            Available Attributes:
            - Category
            - DisplayName
            - Description
            - DefaultValue

            // Technically Supported but not Useful
            - Browsable
            - Localizable

            The `DefaultValue` attribute is used as part of the `Reset` button in Reloaded-Launcher.
        */

        [DisplayName("Show gains as before→after")]
        [Description("When a stat gains points, show the counter as before→after (e.g. 11→14/45) until that stat next gains points.")]
        [DefaultValue(true)]
        public bool ShowGainArrow { get; set; } = true;

        [DisplayName("Show gains as (+x)")]
        [Description("When a stat gains points, append the amount gained (e.g. (+3)) until that stat next gains points. Can be combined with before→after.")]
        [DefaultValue(false)]
        public bool ShowGainPlus { get; set; } = false;

        [DisplayName("Show gains in the stats menu")]
        [Description("Keep showing gain trackers when checking your stats in the pause menu (until the gain display time runs out). When disabled, trackers only show during the events that give the points. Requires p5rpc.lib.")]
        [DefaultValue(false)]
        public bool ShowInMenu { get; set; } = false;

        [DisplayName("Gain display time (seconds)")]
        [Description("How long after a gain its tracker keeps being shown at most (a tracker already on screen never disappears mid-display). 0 = no time limit. Trackers always clear when a new gain happens or when you return to gameplay after the event that gave the points.")]
        [DefaultValue(180)]
        public int GainDisplaySeconds { get; set; } = 180;

        [DisplayName("Debug Mode")]
        [Description("Logs additional information to the console that is useful for debugging.")]
        [DefaultValue(false)]
        public bool DebugEnabled { get; set; } = false;
    }
    
    /// <summary>
    /// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
    /// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
    /// </summary>
    public class ConfiguratorMixin : ConfiguratorMixinBase
    {
        // 
    }
}