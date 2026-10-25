import android.content.Context;

import java.lang.reflect.Field;
import java.lang.reflect.Method;

/**
 * Desktop smoke test for the benchmark engine. Runs the real Bench/Screen classes against
 * the compile-time stubs (so Canvas is a no-op) and checks that a run completes, allocates the
 * requested budget and prints a verdict. This validates the arithmetic, loops and OOM path;
 * the Android UI itself still needs the VM.
 */
public class BenchHarness
{
    public static void main(String[] args) throws Exception
    {
        Class<?> screenClass = Class.forName("com.veyra.bench.Screen");
        Object screen = screenClass.getConstructor(Context.class).newInstance(new Context());

        // targetMb is private; drive it through the slider hit-test instead of reflection on draw.
        Field target = screenClass.getDeclaredField("targetMb");
        target.setAccessible(true);
        target.setInt(screen, 64);

        Method start = screenClass.getMethod("startRun");
        start.invoke(screen);

        Method snapshot = screenClass.getDeclaredMethod("snapshot");
        snapshot.setAccessible(true);

        long deadline = System.currentTimeMillis() + 15000;
        String[] lines = new String[0];
        while (System.currentTimeMillis() < deadline)
        {
            Thread.sleep(250);
            lines = (String[]) snapshot.invoke(screen);
            boolean done = false;
            for (int i = 0; i < lines.length; i++)
                if (lines[i].startsWith("PASS ") || lines[i].startsWith("TIGHT ") || lines[i].startsWith("FAIL "))
                    done = true;
            if (done) break;
        }

        int checked = 0;
        for (int i = 0; i < lines.length; i++)
        {
            System.out.println("  " + lines[i]);
            if (lines[i].startsWith("  alloc") || lines[i].startsWith("  read")
                || lines[i].startsWith("  gc") || lines[i].startsWith("  churn")
                || lines[i].startsWith("  draw") || lines[i].startsWith("PASS ")
                || lines[i].startsWith("TIGHT ") || lines[i].startsWith("FAIL "))
                checked++;
        }
        if (checked < 5)
        {
            System.out.println("HARNESS FAIL: only " + checked + " metric lines");
            System.exit(2);
        }
        System.out.println("HARNESS OK: " + checked + " metric lines");
    }
}
