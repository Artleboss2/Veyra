package android.graphics;
public class Paint {
    public enum Style { FILL, STROKE, FILL_AND_STROKE }
    public Paint() { }
    public void setColor(int color) { }
    public void setTextSize(float size) { }
    public void setAntiAlias(boolean on) { }
    public void setStrokeWidth(float w) { }
    public void setTypeface(Typeface t) { }
    public void setStyle(Style s) { }
}
