package com.veyra.bench;

import android.app.Activity;
import android.os.Bundle;
import android.view.WindowManager;

/**
 * Veyra Bench: a deliberately plain, black-and-white, shader-free benchmark.
 * Everything is drawn by hand on one Canvas so the numbers describe the guest's
 * CPU, heap and drawing speed rather than a widget toolkit.
 */
public class BenchActivity extends Activity
{
    private Screen screen;

    @Override
    public void onCreate(Bundle state)
    {
        super.onCreate(state);
        requestWindowFeature(1); // Window.FEATURE_NO_TITLE
        getWindow().setFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN,
                             WindowManager.LayoutParams.FLAG_FULLSCREEN);
        screen = new Screen(this);
        setContentView(screen);
    }

    @Override
    protected void onDestroy()
    {
        if (screen != null) screen.shutdown();
        super.onDestroy();
    }
}
