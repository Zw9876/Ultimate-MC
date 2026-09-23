import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Socket;

/**
 * Does *this* JVM have internet? Run with the same java.exe the game uses:
 *   runtime\17\bin\java.exe NetTest.java <host> <port>
 * Single-file source launch, so nothing needs compiling.
 */
public class NetTest {
    public static void main(String[] args) {
        String host = args.length > 0 ? args[0] : "mc.hypixel.net";
        int port = args.length > 1 ? Integer.parseInt(args[1]) : 25565;

        InetAddress ip;
        try {
            long t0 = System.nanoTime();
            ip = InetAddress.getByName(host);
            System.out.println("  DNS  OK   " + host + " -> " + ip.getHostAddress()
                    + "  (" + ms(t0) + " ms)");
        } catch (Exception e) {
            System.out.println("  DNS  FAIL " + host + "  " + e.getClass().getSimpleName()
                    + ": " + e.getMessage());
            System.exit(2);
            return;
        }

        try (Socket s = new Socket()) {
            long t0 = System.nanoTime();
            s.connect(new InetSocketAddress(ip, port), 8000);
            System.out.println("  TCP  OK   connected to " + host + ":" + port
                    + "  (" + ms(t0) + " ms)");
        } catch (Exception e) {
            System.out.println("  TCP  FAIL " + host + ":" + port + "  "
                    + e.getClass().getSimpleName() + ": " + e.getMessage());
            System.exit(3);
        }
    }

    private static long ms(long startNanos) {
        return (System.nanoTime() - startNanos) / 1_000_000L;
    }
}
