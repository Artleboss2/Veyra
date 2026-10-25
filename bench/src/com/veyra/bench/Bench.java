package com.veyra.bench;

import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Paint;

/**
 * The measurements. Deliberately dependency free: plain Java arrays, Runtime counters
 * and an offscreen Canvas. Everything runs on one background thread so the screen
 * stays responsive while the numbers are produced.
 */
final class Bench
{
    private static volatile boolean cancel;
    private static volatile boolean busy;
    private static volatile int sink;
    private static Thread worker;

    private Bench() { }

    static boolean start(final Screen screen)
    {
        if (busy) return false;
        busy = true;
        cancel = false;
        worker = new Thread(new Runnable()
        {
            public void run()
            {
                try { work(screen); }
                catch (Throwable t)
                {
                    screen.log("FAIL " + t.getClass().getSimpleName() + ": " + t.getMessage());
                }
                finally
                {
                    busy = false;
                    screen.runFinished();
                }
            }
        }, "veyra-bench");
        worker.setPriority(Thread.NORM_PRIORITY);
        worker.start();
        return true;
    }

    static void stop()
    {
        cancel = true;
        Thread t = worker;
        if (t != null)
        {
            try { t.join(1500); } catch (InterruptedException ignored) { }
        }
        busy = false;
    }

    static void collect(final Screen screen)
    {
        new Thread(new Runnable()
        {
            public void run()
            {
                long t0 = System.nanoTime();
                System.gc();
                long ms = (System.nanoTime() - t0) / 1000000L;
                screen.log("gc pause " + ms + " ms");
            }
        }, "veyra-gc").start();
    }

    // ------------------------------------------------------------------ run

    private static void work(Screen screen)
    {
        Runtime rt = Runtime.getRuntime();
        int target = screen.getTargetMb();
        long heapMax = rt.maxMemory() / (1024L * 1024L);

        screen.log("");
        screen.log("> run  budget " + target + " MB  heap " + heapMax + " MB");

        if (target > heapMax * 0.9)
            screen.log("  warning: budget above 90% of the heap");

        // ---- 1. allocate exactly the budget, touching every page so it is real memory
        final int chunkKb = 256;
        int chunks = Math.max(1, target * 1024 / chunkKb);
        byte[][] blocks = new byte[chunks][];
        long t0 = System.nanoTime();
        int made = 0;
        boolean oom = false;
        try
        {
            for (int i = 0; i < chunks; i++)
            {
                if (cancel) break;
                byte[] b = new byte[chunkKb * 1024];
                for (int o = 0; o < b.length; o += 4096) b[o] = (byte) (o ^ i);
                blocks[i] = b;
                made++;
            }
        }
        catch (OutOfMemoryError e)
        {
            oom = true;
        }
        long allocNs = System.nanoTime() - t0;
        double allocMb = made * chunkKb / 1024.0;
        double allocRate = allocNs > 0 ? allocMb / (allocNs / 1e9) : 0;
        long usedMb = (rt.totalMemory() - rt.freeMemory()) / (1024L * 1024L);

        screen.log((oom ? "FAIL" : "  ok  ") + " allocated " + Screen.whole(allocMb) + " MB"
                + " in " + (allocNs / 1000000L) + " ms");
        if (!oom) screen.log("  alloc  " + Screen.whole(allocRate) + " MB/s");
        screen.log("  heap   " + usedMb + " MB used of " + heapMax + " MB");

        if (oom)
        {
            screen.log("FAIL heap cannot hold this budget");
            screen.log("  lower the slider and run again");
            blocks = null;
            System.gc();
            return;
        }

        // ---- 2. sequential read pass over the committed blocks
        long sum = 0;
        t0 = System.nanoTime();
        int passes = 2;
        for (int p = 0; p < passes && !cancel; p++)
            for (int i = 0; i < made; i++)
            {
                byte[] b = blocks[i];
                for (int o = 0; o < b.length; o += 16) sum = sum * 31 + b[o];
            }
        long readNs = System.nanoTime() - t0;
        double readMb = (double) made * chunkKb / 1024.0 * passes;
        double readRate = readNs > 0 ? readMb / (readNs / 1e9) : 0;
        screen.log("  read   " + Screen.whole(readRate) + " MB/s  (sum " + Long.toHexString(sum & 0xFFFF) + ")");

        // ---- 3. full GC with the budget still held
        t0 = System.nanoTime();
        System.gc();
        long gcMs = (System.nanoTime() - t0) / 1000000L;
        screen.log("  gc     " + gcMs + " ms  full collect");

        // ---- 4. object churn: short lived arrays, the usual Android allocation pattern
        long churnEnd = System.nanoTime() + 1200000000L;
        long made2 = 0;
        int local = 0;
        while (System.nanoTime() < churnEnd && !cancel)
        {
            int[] junk = new int[16];
            junk[0] = (int) made2;
            local += junk[0];
            made2++;
        }
        sink = local;
        double churnRate = made2 / 1.2;
        screen.log("  churn  " + Screen.whole(churnRate) + " obj/s");

        // ---- 5. canvas throughput on a small offscreen bitmap
        Bitmap bmp = null;
        double fps = 0;
        try
        {
            bmp = Bitmap.createBitmap(256, 256, Bitmap.Config.ARGB_8888);
            Canvas c = new Canvas(bmp);
            Paint p = new Paint();
            p.setAntiAlias(false);
            p.setStrokeWidth(1f);
            long d0 = System.nanoTime();
            long budget = 1500000000L;
            int frames = 0;
            while (System.nanoTime() - d0 < budget && !cancel)
            {
                c.drawColor(0xFF000000);
                for (int k = 0; k < 24; k++)
                {
                    p.setColor(k % 2 == 0 ? 0xFFFFFFFF : 0xFF808080);
                    c.drawLine(0, k * 10, 256, 256 - k * 10, p);
                }
                frames++;
            }
            long dns = System.nanoTime() - d0;
            fps = dns > 0 ? frames / (dns / 1e9) : 0;
            screen.log("  draw   " + Screen.one(fps) + " fps  (256x256 canvas)");
        }
        finally
        {
            if (bmp != null) bmp.recycle();
        }

        // ---- 6. verdict
        String tag;
        String verdict;
        if (allocRate >= 150 && fps >= 20) { tag = "PASS"; verdict = "comfortable"; }
        else if (allocRate >= 40 && fps >= 8) { tag = "TIGHT"; verdict = "playable but slow"; }
        else { tag = "FAIL"; verdict = "too slow for this budget"; }

        screen.log(tag + " " + verdict);
        screen.log("  budget " + target + " MB  alloc " + Screen.whole(allocRate) + " MB/s");

        blocks = null;
        t0 = System.nanoTime();
        System.gc();
        long releaseMs = (System.nanoTime() - t0) / 1000000L;
        screen.log("  released in " + releaseMs + " ms");
    }
}
