using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Atri.Core;

public readonly record struct VisualImageInfo(string MimeType,int Width,int Height);

/// <summary>A confirmed, in-memory raster attachment. Contains no filesystem path.</summary>
public sealed record VisualAttachment(string MimeType,string Base64,string Sha256,string? OriginalName=null)
{
    public const int MaximumInputBytes=10*1024*1024;
    public const int MaximumEncodedBytes=4*1024*1024;
    public const int MaximumDimension=1600;
    public const long MaximumSourcePixels=80_000_000;

    /// <summary>Validates a source before the desktop's real image decoder runs.</summary>
    public static VisualImageInfo InspectInput(ReadOnlySpan<byte> bytes)
    {
        if(bytes.IsEmpty||bytes.Length>MaximumInputBytes)throw new InvalidDataException("图片须为非空文件，原始大小最多10 MiB。");
        int width,height;
        if(bytes.Length>=12&&bytes[..4].SequenceEqual("RIFF"u8)&&bytes.Slice(8,4).SequenceEqual("WEBP"u8))
            throw new InvalidDataException("此版本未部署 WebP 解码器，请先转换为 PNG 或 JPEG。");
        if(bytes.Length>=33&&bytes[..8].SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))
        {
            if(BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(8,4))!=13||!bytes.Slice(12,4).SequenceEqual("IHDR"u8))throw new InvalidDataException("PNG头部不完整。");
            width=checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16,4)));
            height=checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20,4)));
            Bounds(width,height);return new("image/png",width,height);
        }
        if(bytes.Length>=4&&bytes[0]==255&&bytes[1]==216)
        {
            var position=2;
            while(position<bytes.Length)
            {
                if(bytes[position++]!=255)throw new InvalidDataException("JPEG标记格式错误。");
                while(position<bytes.Length&&bytes[position]==255)position++;
                if(position>=bytes.Length)break;
                var marker=bytes[position++];
                if(marker==217||marker==218)break;
                if(marker==1||marker is >=208 and <=215)continue;
                if(position+2>bytes.Length)break;
                var length=BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(position,2));
                if(length<2||position+length>bytes.Length)throw new InvalidDataException("JPEG段被截断。");
                if(marker is 192 or 193 or 194 or 195 or 197 or 198 or 199 or 201 or 202 or 203 or 205 or 206 or 207)
                {
                    if(length<8)throw new InvalidDataException("JPEG尺寸段不完整。");
                    height=BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(position+3,2));
                    width=BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(position+5,2));
                    Bounds(width,height);return new("image/jpeg",width,height);
                }
                position+=length;
            }
            throw new InvalidDataException("未找到有效JPEG尺寸。");
        }
        if(bytes.Length>=54&&bytes[0]=='B'&&bytes[1]=='M')
        {
            var dib=BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(14,4));
            if(dib<40)throw new InvalidDataException("此版本不支持旧式BMP头，请转换为PNG。");
            width=BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(18,4));
            var rawHeight=BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(22,4));
            if(rawHeight==int.MinValue)throw new InvalidDataException("BMP尺寸无效。");
            height=Math.Abs(rawHeight);Bounds(width,height);return new("image/bmp",width,height);
        }
        throw new InvalidDataException("仅支持真实 PNG、JPEG、BMP 图片；文件扩展名不能代替格式校验。");
    }

    private static void Bounds(int width,int height)
    {
        if(width<1||height<1||width>50000||height>50000||(long)width*height>MaximumSourcePixels)
            throw new InvalidDataException("图片尺寸无效或超过安全解码上限（8000万像素）。");
    }

    /// <summary>Reads only a file explicitly selected by the user; never exposed as a model tool.</summary>
    public static byte[] ReadSelectedFile(string selectedPath)
    {
        using var stream=new FileStream(selectedPath,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length is <1 or >MaximumInputBytes)throw new InvalidDataException("原图大小最多10 MiB。");
        var bytes=new byte[checked((int)stream.Length)];stream.ReadExactly(bytes);
        if(stream.ReadByte()!=-1)throw new InvalidDataException("读取期间图片发生变化，请重新选择。");
        InspectInput(bytes);return bytes;
    }

    /// <summary>Creates an attachment from a prepared, decoded/re-encoded PNG/JPEG.</summary>
    public static VisualAttachment FromBytes(byte[] encoded,string? originalName=null)
    {
        var info=ValidateEncoded(encoded);
        var name=string.IsNullOrWhiteSpace(originalName)?null:Path.GetFileName(originalName);
        if(name?.Length>120)name=name[..120];
        return new(info.MimeType,Convert.ToBase64String(encoded),Convert.ToHexStringLower(SHA256.HashData(encoded)),name);
    }

    /// <summary>For already-prepared files; normal user input uses VisualInputView to resize/re-encode.</summary>
    public static VisualAttachment FromFile(string selectedPath)=>FromBytes(ReadSelectedFile(selectedPath),Path.GetFileName(selectedPath));

    public void Validate()
    {
        if(Base64.Length>((MaximumEncodedBytes+2)/3)*4)throw new InvalidDataException("编码后的图片超过4 MiB。");
        byte[] encoded;try{encoded=Convert.FromBase64String(Base64);}catch(FormatException ex){throw new InvalidDataException("图片Base64无效。",ex);}
        var info=ValidateEncoded(encoded);
        if(MimeType!=info.MimeType||!string.Equals(Sha256,Convert.ToHexStringLower(SHA256.HashData(encoded)),StringComparison.Ordinal))
            throw new InvalidDataException("图片类型或摘要不匹配。");
    }

    private static VisualImageInfo ValidateEncoded(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length>MaximumEncodedBytes)throw new InvalidDataException("图片尚未完成压缩，编码后最多4 MiB。");
        var info=InspectInput(bytes);
        if(info.MimeType is not ("image/png" or "image/jpeg")||Math.Max(info.Width,info.Height)>MaximumDimension)
            throw new InvalidDataException("图片须先在视觉附件窗口缩放为最长1600像素的PNG/JPEG。");
        if(info.MimeType=="image/jpeg"&&(bytes.Length<4||bytes[^2]!=255||bytes[^1]!=217))throw new InvalidDataException("JPEG结束标记缺失。");
        if(info.MimeType=="image/png")
        {
            var offset=8;bool imageData=false,ended=false;var chunks=0;
            while(offset+12<=bytes.Length&&++chunks<=4096)
            {
                var length=BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset,4));
                if(length>int.MaxValue||length>bytes.Length-offset-12)throw new InvalidDataException("PNG数据块被截断。");
                var count=(int)length;var type=bytes.Slice(offset+4,4);
                if(type.SequenceEqual("IDAT"u8))imageData=true;
                var expected=BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset+8+count,4));
                uint crc=0xffffffff;
                foreach(var value in bytes.Slice(offset+4,count+4))
                {crc^=value;for(int bit=0;bit<8;bit++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0u);}
                if((crc^0xffffffff)!=expected)throw new InvalidDataException("PNG数据块校验失败。");
                offset+=count+12;
                if(type.SequenceEqual("IEND"u8)){ended=count==0&&offset==bytes.Length;break;}
            }
            if(!imageData||!ended)throw new InvalidDataException("PNG图像数据或结束块缺失。");
        }
        return info;
    }
}
