/// <summary>Grava um quadro ARGB como BMP 32 bits (abre direto no Windows), com ampliação inteira.</summary>
public static class BmpWriter
{
    public static void Save(string path, uint[] argb, int width, int height, int scale)
    {
        int w = width * scale, h = height * scale;
        int dataSize = w * h * 4;

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        // Cabeçalho do arquivo (14 bytes)
        bw.Write((byte)'B'); bw.Write((byte)'M');
        bw.Write(54 + dataSize);
        bw.Write(0);
        bw.Write(54);
        // Cabeçalho da imagem (40 bytes); altura negativa = linhas de cima para baixo
        bw.Write(40); bw.Write(w); bw.Write(-h);
        bw.Write((short)1); bw.Write((short)32);
        bw.Write(0); bw.Write(dataSize);
        bw.Write(2835); bw.Write(2835);
        bw.Write(0); bw.Write(0);

        var row = new byte[w * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                uint c = argb[y * width + x];
                for (int s = 0; s < scale; s++)
                {
                    int i = (x * scale + s) * 4;
                    row[i] = (byte)c;              // B
                    row[i + 1] = (byte)(c >> 8);   // G
                    row[i + 2] = (byte)(c >> 16);  // R
                    row[i + 3] = 0xFF;
                }
            }
            for (int s = 0; s < scale; s++) bw.Write(row);
        }
    }
}
