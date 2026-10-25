package android.view;
import android.content.Context;
import android.graphics.Canvas;
public class View {
    public View(Context c) { }
    protected void onDraw(Canvas c) { }
    public boolean onTouchEvent(MotionEvent e) { return false; }
    public void postInvalidate() { }
    public void invalidate() { }
    public int getWidth() { return 480; }
    public int getHeight() { return 800; }
}
