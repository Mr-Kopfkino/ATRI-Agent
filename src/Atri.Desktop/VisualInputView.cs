using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Atri.Core;
using Microsoft.Win32;

namespace Atri.Desktop;

/// <summary>User-selected, confirmed visual input; performs no network request.</summary>
public sealed class VisualInputView : Window
{
    private readonly Image preview=new(){Stretch=Stretch.Uniform};
    private readonly TextBlock status=new(){Text="选择一张 PNG、JPEG 或 BMP（最多10 MiB）。图片只在内存中准备。",TextWrapping=TextWrapping.Wrap,FontSize=15,Foreground=Brushes.DarkSlateBlue};
    private readonly Button confirm=new(){Content="确认附加",Padding=new Thickness(20,9,20,9),IsEnabled=false};
    private readonly Button select=new(){Content="选择图片",Padding=new Thickness(16,9,16,9)};
    private VisualAttachment? attachment;
    private int revision;
    private bool closing;

    internal VisualInputView(Window owner,bool chooseOnLoad=true)
    {
        Owner=owner;Title="ATRI · 视觉附件";Width=Math.Min(880,SystemParameters.WorkArea.Width-40);Height=Math.Min(650,SystemParameters.WorkArea.Height-40);
        MinWidth=480;MinHeight=360;WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=Brushes.AliceBlue;
        var panel=new DockPanel{Margin=new Thickness(20)};Content=panel;
        DockPanel.SetDock(status,Dock.Top);status.Margin=new Thickness(0,0,0,14);panel.Children.Add(status);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
        DockPanel.SetDock(buttons,Dock.Bottom);panel.Children.Add(buttons);
        var clear=new Button{Content="清除",Margin=new Thickness(8,0,8,0),Padding=new Thickness(16,9,16,9)};
        var cancel=new Button{Content="取消",Margin=new Thickness(8,0,0,0),Padding=new Thickness(16,9,16,9),IsCancel=true};
        buttons.Children.Add(select);buttons.Children.Add(clear);buttons.Children.Add(confirm);buttons.Children.Add(cancel);
        panel.Children.Add(new Border{Background=Brushes.White,BorderBrush=Brushes.LightBlue,BorderThickness=new Thickness(1),Padding=new Thickness(8),Child=preview});
        select.Click+=async(_,_)=>await SelectAsync();
        clear.Click+=(_,_)=>{revision++;attachment=null;preview.Source=null;confirm.IsEnabled=true;status.Text="已清除附件；点击确认应用清除，用户原图未修改。";};
        confirm.Click+=(_,_)=>DialogResult=true;
        Closed+=(_,_)=>{closing=true;revision++;if(DialogResult!=true)attachment=null;};
        if(chooseOnLoad)Loaded+=async(_,_)=>await SelectAsync();
    }

    public static VisualAttachment? Choose(Window owner)
    {return Choose(owner,out var selected)?selected:null;}

    public static bool Choose(Window owner,out VisualAttachment? selected)
    {var dialog=new VisualInputView(owner);var accepted=dialog.ShowDialog()==true;selected=accepted?dialog.attachment:null;return accepted;}

    private async Task SelectAsync()
    {
        var dialog=new OpenFileDialog{Title="选择要交给亚托莉查看的图片",Filter="图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|WebP（此版本不支持）|*.webp",Multiselect=false,CheckFileExists=true};
        if(dialog.ShowDialog(this)!=true)return;
        var request=++revision;var selected=dialog.FileName;select.IsEnabled=false;confirm.IsEnabled=false;status.Text="正在读取与缩放图片…";
        try
        {
            var prepared=await Task.Run(()=>Prepare(VisualAttachment.ReadSelectedFile(selected),Path.GetFileName(selected)));
            if(closing||request!=revision)return;
            attachment=prepared;
            var bytes=Convert.FromBase64String(prepared.Base64);using var stream=new MemoryStream(bytes,false);
            var image=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];image.Freeze();preview.Source=image;
            confirm.IsEnabled=true;status.Text=$"{prepared.OriginalName} · {image.PixelWidth} × {image.PixelHeight} · {bytes.Length/1024d:F0} KiB\n仅发送确认后的这一张图片。可以清除或取消。";
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException or OverflowException or COMException or InvalidOperationException)
        {if(!closing&&request==revision){attachment=null;preview.Source=null;status.Text="无法准备图片："+ex.Message;}}
        finally{if(!closing)select.IsEnabled=true;}
    }

    /// <summary>Real WIC decode, bounded resize and fresh metadata-free re-encode in memory.</summary>
    public static VisualAttachment Prepare(byte[] input,string? originalName=null)
    {
        var info=VisualAttachment.InspectInput(input);
        var scale=Math.Min(1d,VisualAttachment.MaximumDimension/(double)Math.Max(info.Width,info.Height));
        using var stream=new MemoryStream(input,false);
        var decoded=new BitmapImage();decoded.BeginInit();decoded.CacheOption=BitmapCacheOption.OnLoad;decoded.CreateOptions=BitmapCreateOptions.PreservePixelFormat;
        decoded.StreamSource=stream;decoded.DecodePixelWidth=Math.Max(1,(int)Math.Floor(info.Width*scale));decoded.EndInit();decoded.Freeze();
        BitmapSource source=decoded;
        if(Math.Max(source.PixelWidth,source.PixelHeight)>VisualAttachment.MaximumDimension)
        {var factor=VisualAttachment.MaximumDimension/(double)Math.Max(source.PixelWidth,source.PixelHeight);var smaller=new TransformedBitmap(source,new ScaleTransform(factor,factor));smaller.Freeze();source=smaller;}
        var converted=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);converted.Freeze();
        int width=converted.PixelWidth,height=converted.PixelHeight,stride=checked(width*4);
        var pixels=new byte[checked(stride*height)];converted.CopyPixels(pixels,stride,0);
        // Fresh pixel-only BitmapSource prevents user EXIF/GPS/ancillary metadata
        // from being carried into the outgoing image.
        var fresh=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,stride);fresh.Freeze();
        static byte[] Encode(BitmapSource bitmap,BitmapEncoder encoder)
        {encoder.Frames.Add(BitmapFrame.Create(bitmap));using var target=new MemoryStream();encoder.Save(target);return target.ToArray();}
        var encoded=Encode(fresh,new PngBitmapEncoder());
        if(encoded.Length<=VisualAttachment.MaximumEncodedBytes)return VisualAttachment.FromBytes(encoded,originalName);
        // When PNG exceeds the budget, flatten transparent pixels on white and
        // use a bounded set of JPEG qualities; never silently truncate bytes.
        for(int i=0;i<pixels.Length;i+=4)
        {int alpha=pixels[i+3];for(int channel=0;channel<3;channel++)pixels[i+channel]=(byte)((pixels[i+channel]*alpha+255*(255-alpha)+127)/255);pixels[i+3]=255;}
        var opaque=BitmapSource.Create(width,height,96,96,PixelFormats.Bgr32,null,pixels,stride);opaque.Freeze();
        foreach(var quality in new[]{90,80,65})
        {encoded=Encode(opaque,new JpegBitmapEncoder{QualityLevel=quality});if(encoded.Length<=VisualAttachment.MaximumEncodedBytes)return VisualAttachment.FromBytes(encoded,originalName);}
        throw new InvalidDataException("缩放后图片仍超过4 MiB，请选择较简单的画面。");
    }
}
