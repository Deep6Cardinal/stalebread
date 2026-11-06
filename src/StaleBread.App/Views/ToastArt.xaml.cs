using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
namespace StaleBread.App.Views;
public partial class ToastArt : UserControl
{
    private bool watching;
    public ToastArt()
    {
        InitializeComponent(); IsVisibleChanged += (_, _) => { if (!IsVisible) SetWatching(false); };
        if (SystemParameters.HighContrast)
        { Crust.Fill = SystemColors.WindowBrush; Crust.Stroke = SystemColors.WindowTextBrush; Crumb.Fill = SystemColors.WindowBrush;
          LeftEye.Fill = RightEye.Fill = Smile.Stroke = SystemColors.WindowTextBrush; }
    }
    public void SetWatching(bool enabled)
    {
        if (enabled == watching) return; watching = enabled;
        Gaze.BeginAnimation(TranslateTransform.XProperty, null);
        if (!enabled) return;
        var scan = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(8), RepeatBehavior = RepeatBehavior.Forever };
        foreach (var (seconds, x) in new[] { (0d, 0d), (.6, -6d), (2.8, -6d), (3.5, 6d), (5.8, 6d), (6.5, 0d), (8d, 0d) })
            scan.KeyFrames.Add(new LinearDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds))));
        Timeline.SetDesiredFrameRate(scan, 15); Gaze.BeginAnimation(TranslateTransform.XProperty, scan);
    }
    public void Pop(bool animate)
    {
        Toaster.Visibility = Visibility.Visible; SetWatching(false);
        BreadY.BeginAnimation(TranslateTransform.YProperty, null); BreadY.Y = -12;
        if (!animate) return;
        var pop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(900), FillBehavior = FillBehavior.Stop };
        pop.KeyFrames.Add(new LinearDoubleKeyFrame(55, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(-20, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(-12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)), new BounceEase { Bounces = 1, EasingMode = EasingMode.EaseOut }));
        Timeline.SetDesiredFrameRate(pop, 15); BreadY.BeginAnimation(TranslateTransform.YProperty, pop);
    }
    public void Stop() { SetWatching(false); BreadY.BeginAnimation(TranslateTransform.YProperty, null); }
}
