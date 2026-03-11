namespace ImageUiSlicer.Models;

public readonly record struct BBox(int X, int Y, int W, int H)
{
    public int Right => X + W;

    public int Bottom => Y + H;
}
