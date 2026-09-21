using System.Buffers.Binary;
namespace NetCat.Core;

public static class IconDirectory
{
    public static byte[] Image(byte[] ico,int preferredSize=32)
    {
        var data=ico.AsSpan();
        if(data.Length<6 || BinaryPrimitives.ReadUInt16LittleEndian(data)!=0 || BinaryPrimitives.ReadUInt16LittleEndian(data[2..])!=1) throw new InvalidDataException("Некорректный ICO.");
        var count=BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if(count==0 || 6L+16L*count>data.Length) throw new InvalidDataException("Обрезан каталог ICO.");
        int offset=0,length=0,best=int.MaxValue;
        for(int i=0;i<count;i++)
        {
            var entry=data.Slice(6+i*16,16); int width=entry[0]==0?256:entry[0],height=entry[1]==0?256:entry[1];
            uint size=BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]),start=BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if(size==0 || start<6+count*16 || (ulong)start+size>(ulong)data.Length) throw new InvalidDataException("Обрезано изображение ICO.");
            int distance=Math.Abs(width-preferredSize)+Math.Abs(height-preferredSize);
            if(distance<best) { best=distance; offset=(int)start; length=(int)size; }
        }
        return data.Slice(offset,length).ToArray();
    }
}
