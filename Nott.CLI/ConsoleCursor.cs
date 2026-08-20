namespace Nott.CLI;

public static class ConsoleCursor
{
    public static void SetVisible(bool state)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                Console.CursorVisible = state;
                return;
            }
            catch
            {
                /* fall through to ANSI */
            }
        }

        Console.Write(state ? "\x1B[?25h" : "\x1B[?25l");
    }
}