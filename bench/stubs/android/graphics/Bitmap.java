package android.graphics;
public class Bitmap {
    public enum Config { ARGB_8888, RGB_565 }
    public static Bitmap createBitmap(int w, int h, Config c) { return new Bitmap(); }
    public void recycle() { }
}
