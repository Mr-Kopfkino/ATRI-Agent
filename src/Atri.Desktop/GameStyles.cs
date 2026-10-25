using System.Windows;
using System.Windows.Markup;
namespace Atri.Desktop;
internal static class GameStyles
{
    internal static ResourceDictionary Create() => (ResourceDictionary)XamlReader.Parse("""
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
 <Style TargetType="TabItem">
  <Setter Property="Width" Value="228"/><Setter Property="Height" Value="49"/>
  <Setter Property="Foreground" Value="White"/><Setter Property="Background" Value="#36B5F2"/><Setter Property="Padding" Value="16,12"/>
  <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabItem"><Grid MinHeight="49"><Polygon Points="0,49 24,0 320,0 320,49" Stretch="Fill" Fill="{TemplateBinding Background}"/><ContentPresenter ContentSource="Header" HorizontalAlignment="Center" VerticalAlignment="Center" Margin="23,8,14,8"/></Grid><ControlTemplate.Triggers><Trigger Property="IsSelected" Value="True"><Setter Property="Background" Value="#F4FCFF"/><Setter Property="Foreground" Value="#156AB7"/></Trigger><Trigger Property="IsMouseOver" Value="True"><Setter Property="Foreground" Value="#143D72"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType="TextBox"><Setter Property="BorderBrush" Value="#8ECBE9"/><Setter Property="Foreground" Value="#174B7A"/><Setter Property="Background" Value="#F9FEFF"/><Setter Property="CaretBrush" Value="#138ED1"/><Setter Property="SelectionBrush" Value="#77D0F5"/></Style>
 <Style TargetType="PasswordBox"><Setter Property="BorderBrush" Value="#8ECBE9"/><Setter Property="Foreground" Value="#174B7A"/><Setter Property="Background" Value="#F9FEFF"/></Style>
 <Style TargetType="Button"><Setter Property="Background" Value="#E6F7FF"/><Setter Property="BorderBrush" Value="#35B7F5"/><Setter Property="HorizontalContentAlignment" Value="Center"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button"><Border x:Name="surface" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"><ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center" Margin="{TemplateBinding Padding}"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="surface" Property="Opacity" Value="0.78"/></Trigger><Trigger Property="IsPressed" Value="True"><Setter TargetName="surface" Property="Opacity" Value="0.55"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter TargetName="surface" Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
</ResourceDictionary>
""");
}
