package android.app;
import android.content.Context;
import android.os.Bundle;
import android.view.View;
import android.view.Window;
public class Activity extends Context {
    public static final int FEATURE_NO_TITLE = 1;
    public void onCreate(Bundle state) { }
    protected void onDestroy() { }
    public boolean requestWindowFeature(int feature) { return true; }
    public Window getWindow() { return null; }
    public void setContentView(View view) { }
}
