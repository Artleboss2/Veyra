package com.veyra.bench;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.Typeface;
import android.os.Build;
import android.view.MotionEvent;
import android.view.View;

import java.util.ArrayList;

/**
 * One hand-drawn screen: header, scrolling log, a RAM slider and three buttons.
 * Black background, white text, thin white rules. No images, no gradients, no shaders.
 */
public class Screen extends View
{
    private static final int WHITE = 0xFFFFFFFF;
    private static final int DIM = 0xFF8A8A8A;
    private static final int FAINT = 0xFF3A3A3A;

    private final Paint fill = new Paint();
    private final Paint stroke = new Paint();
    private final Paint text = new Paint();

    private final ArrayList<String> log = new ArrayList<String>();
    private static final int LOG_MAX = 60;

    private final int heapMaxMb;
    private final int targetMaxMb;

    private int targetMb = 128;
    private volatile boolean running;
    private volatile boolean closing;

    private float scale = 1f;

    // Hit boxes, recomputed on every draw.
    private float trackX0, trackX1, trackY;
    private float[] runBtn, gcBtn, clearBtn;
    private boolean dragging;

    public Screen(Context c)
    {
        super(c);
        Runtime rt = Runtime.getRuntime();
        heapMaxMb = (int) (rt.maxMemory() / (1024L * 1024L));
        targetMaxMb = Math.max(32, Math.min(1024, (int) (heapMaxMb * 0.8)));
        targetMb = Math.max(32, Math.min(128, targetMaxMb));

        fill.setAntiAlias(false);
        fill.setStyle(Paint.Style.FILL);
        stroke.setAntiAlias(false);
        stroke.setStyle(Paint.Style.STROKE);
        stroke.setStrokeWidth(1f);
        text.setAntiAlias(true);
        text.setTypeface(Typeface.MONOSPACE);

        log("veyra bench 1.0");
        log("android " + Build.VERSION.RELEASE + "  api " + Build.VERSION.SDK_INT);
        log("device  " + Build.MODEL);
        log("heap max " + heapMaxMb + " MB  cores " + rt.availableProcessors());
        log("");
        log("choose a RAM budget, then RUN.");
        log("the app allocates exactly that much,");
        log("then measures read speed, gc pause,");
        log("object churn and canvas fps.");
    }

    public int getTargetMb() { return targetMb; }

    public void shutdown()
    {
        closing = true;
        Bench.stop();
    }

    // ------------------------------------------------------------------ log

    public void log(String line)
    {
        synchronized (log)
        {
            log.add(line == null ? "" : line);
            while (log.size() > LOG_MAX) log.remove(0);
        }
        if (!closing) postInvalidate();
    }

    private String[] snapshot()
    {
        synchronized (log)
        {
            String[] out = new String[log.size()];
            for (int i = 0; i < out.length; i++) out[i] = log.get(i);
            return out;
        }
    }

    // ------------------------------------------------------------------ inputs

    public void startRun()
    {
        if (running) return;
        running = true;
        postInvalidate();
        if (!Bench.start(this)) running = false;
    }

    public void runFinished()
    {
        running = false;
        postInvalidate();
    }

    @Override
    public boolean onTouchEvent(MotionEvent e)
    {
        float x = e.getX();
        float y = e.getY();
        int action = e.getAction();

        if (action == 0)
        {
            if (y > trackY - 26 * scale && y < trackY + 26 * scale && x >= trackX0 - 10 && x <= trackX1 + 10)
            {
                dragging = true;
                setTargetFromX(x);
                return true;
            }
            return true;
        }
        if (action == 2)
        {
            if (dragging) setTargetFromX(x);
            return true;
        }
        if (action == 1)
        {
            if (dragging)
            {
                dragging = false;
                postInvalidate();
                return true;
            }
            if (inside(runBtn, x, y)) { startRun(); return true; }
            if (inside(gcBtn, x, y))
            {
                log("gc requested");
                Bench.collect(this);
                return true;
            }
            if (inside(clearBtn, x, y))
            {
                synchronized (log) { log.clear(); }
                running = false;
                postInvalidate();
                return true;
            }
        }
        return true;
    }

    private boolean inside(float[] box, float x, float y)
    {
        return box != null && x >= box[0] && x <= box[2] && y >= box[1] && y <= box[3];
    }

    private void setTargetFromX(float x)
    {
        float t = (x - trackX0) / Math.max(1f, trackX1 - trackX0);
        if (t < 0f) t = 0f;
        if (t > 1f) t = 1f;
        int span = targetMaxMb - 32;
        int v = 32 + (int) (t * span);
        v = (v / 8) * 8;
        if (v < 32) v = 32;
        if (v > targetMaxMb) v = targetMaxMb;
        if (v != targetMb)
        {
            targetMb = v;
            postInvalidate();
        }
    }

    // ------------------------------------------------------------------ paint

    @Override
    protected void onDraw(Canvas c)
    {
        int w = getWidth();
        int h = getHeight();
        float base = Math.min(w / 480f, h / 800f);
        scale = Math.max(0.6f, Math.min(2.4f, base));

        c.drawColor(0xFF000000);

        float pad = 18 * scale;
        float y = 26 * scale;

        text.setTextSize(16 * scale);
        text.setColor(WHITE);
        c.drawText("VEYRA BENCH", pad, y, text);
        text.setTextSize(9 * scale);
        text.setColor(DIM);
        c.drawText("APK memory + speed test - dalvik only, no native code", pad, y + 14 * scale, text);

        y += 24 * scale;
        rule(c, pad, y, w - pad);
        y += 16 * scale;

        // ---- log
        String[] lines = snapshot();
        float lineH = 13 * scale;
        text.setTextSize(9.5f * scale);
        int room = (int) ((h * 0.50f - y) / lineH);
        if (room < 1) room = 1;
        int from = Math.max(0, lines.length - room);
        for (int i = from; i < lines.length; i++)
        {
            String s = lines[i];
            if (s.startsWith("PASS")) text.setColor(WHITE);
            else if (s.startsWith("TIGHT") || s.startsWith("FAIL")) text.setColor(DIM);
            else if (s.startsWith(">")) text.setColor(WHITE);
            else text.setColor(DIM);
            c.drawText(s, pad, y, text);
            y += lineH;
        }

        // ---- slider
        float controlsY = h - 168 * scale;
        if (controlsY < y + 20 * scale) controlsY = y + 20 * scale;
        if (controlsY > h - 150 * scale) controlsY = h - 150 * scale;

        text.setTextSize(9 * scale);
        text.setColor(DIM);
        c.drawText("TARGET RAM", pad, controlsY, text);
        text.setColor(WHITE);
        text.setTextSize(15 * scale);
        c.drawText(targetMb + " MB", w - pad - 70 * scale, controlsY + 2 * scale, text);
        text.setTextSize(8 * scale);
        text.setColor(DIM);
        c.drawText("heap " + heapMaxMb + " MB", w - pad - 70 * scale, controlsY + 15 * scale, text);

        trackX0 = pad;
        trackX1 = w - pad;
        trackY = controlsY + 30 * scale;
        stroke.setColor(FAINT);
        c.drawLine(trackX0, trackY, trackX1, trackY, stroke);

        float t = (targetMb - 32) / (float) Math.max(1, targetMaxMb - 32);
        float knob = trackX0 + t * (trackX1 - trackX0);
        fill.setColor(WHITE);
        c.drawRect(knob - 1.5f * scale, trackY - 9 * scale, knob + 1.5f * scale, trackY + 9 * scale, fill);

        // ---- buttons
        float by = trackY + 26 * scale;
        float bh = 30 * scale;
        float gap = 8 * scale;
        float bw = (trackX1 - trackX0 - 2 * gap) / 3f;
        runBtn = button(c, trackX0, by, bw, bh, running ? "RUNNING" : "RUN", !running);
        gcBtn = button(c, trackX0 + bw + gap, by, bw, bh, "GC", true);
        clearBtn = button(c, trackX0 + 2 * (bw + gap), by, bw, bh, "CLEAR", true);

        // ---- footer
        text.setTextSize(8 * scale);
        text.setColor(FAINT);
        c.drawText("budget is committed per run - the toast is the verdict", pad, h - 12 * scale, text);
    }

    private float[] button(Canvas c, float x, float y, float w, float h, String label, boolean enabled)
    {
        stroke.setColor(enabled ? WHITE : FAINT);
        c.drawRect(x, y, x + w, y + h, stroke);
        text.setTextSize(10 * scale);
        text.setColor(enabled ? WHITE : FAINT);
        float textW = label.length() * 6.1f * scale;
        c.drawText(label, x + (w - textW) / 2f, y + h / 2f + 3.5f * scale, text);
        return new float[] { x, y, x + w, y + h };
    }

    private void rule(Canvas c, float x0, float x1, float y)
    {
        stroke.setColor(FAINT);
        c.drawLine(x0, y, x1, y, stroke);
    }

    // ------------------------------------------------------------------ formatting

    static String one(double v)
    {
        long n = Math.round(v * 10.0);
        long whole = n / 10;
        long frac = n % 10;
        if (frac < 0) frac = -frac;
        return whole + "." + frac;
    }

    static String whole(double v)
    {
        return Long.toString(Math.round(v));
    }
}
