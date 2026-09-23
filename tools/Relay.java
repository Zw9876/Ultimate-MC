import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.util.Hashtable;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;

/**
 * A plain TCP relay, so machines that cannot reach an outside Minecraft server
 * can reach it through one that can.
 *
 * Minecraft's protocol is just a TCP stream, so this never has to understand a
 * single packet of it: accept a client, open a second socket to the real server,
 * pump bytes both ways until either end hangs up.
 *
 * Run it on a machine whose Java CAN already reach the server -- check that with
 * Diagnose-MinecraftNet.ps1 first. If java.exe itself is what is blocked, a relay
 * on that same machine is blocked too, and this will not help.
 *
 * No build step: the bundled JDKs run it straight from source.
 *
 *   runtime\17\bin\java.exe tools\Relay.java 25565 play.example.net
 *   runtime\17\bin\java.exe tools\Relay.java 25565 play.example.net 25565
 *
 * Then everyone else puts <this-machine's-LAN-ip> in their server list.
 */
public class Relay {

    private static final AtomicInteger LIVE = new AtomicInteger();
    private static final AtomicInteger TOTAL = new AtomicInteger();

    public static void main(String[] args) throws Exception {
        if (args.length < 2) {
            System.out.println("usage: java Relay.java <listenPort> <targetHost> [targetPort]");
            System.out.println("       omit targetPort to look up the server's SRV record");
            return;
        }

        int listenPort = Integer.parseInt(args[0]);
        String targetHost = args[1];
        int targetPort;

        if (args.length > 2) {
            targetPort = Integer.parseInt(args[2]);
        } else {
            String[] srv = lookupSrv(targetHost);
            if (srv != null) {
                targetHost = srv[0];
                targetPort = Integer.parseInt(srv[1]);
                System.out.println("SRV record -> " + targetHost + ":" + targetPort);
            } else {
                targetPort = 25565;
            }
        }

        // Fail loudly here rather than per-connection: if the relay host cannot
        // reach the server, nothing downstream of it can work either.
        System.out.print("Testing " + targetHost + ":" + targetPort + " ... ");
        try (Socket probe = new Socket()) {
            probe.connect(new InetSocketAddress(targetHost, targetPort), 8000);
            System.out.println("reachable.");
        } catch (IOException e) {
            System.out.println("UNREACHABLE (" + e.getMessage() + ")");
            System.out.println();
            System.out.println("This machine cannot reach the server either, so relaying from");
            System.out.println("it cannot work. Run Diagnose-MinecraftNet.ps1 here first.");
            System.exit(1);
            return;
        }

        final String host = targetHost;
        final int port = targetPort;

        ServerSocket listener = new ServerSocket(listenPort);
        System.out.println();
        System.out.println("Relaying  *:" + listenPort + "  ->  " + host + ":" + port);
        System.out.println("Point the other machines at this box's LAN address on port "
                + listenPort + ".");
        System.out.println("Ctrl+C to stop.");
        System.out.println();

        while (true) {
            Socket client = listener.accept();
            Thread t = new Thread(() -> handle(client, host, port), "relay-" + TOTAL.incrementAndGet());
            t.setDaemon(false);
            t.start();
        }
    }

    private static void handle(Socket client, String host, int port) {
        String who = client.getInetAddress().getHostAddress() + ":" + client.getPort();
        Socket server = null;
        AtomicLong up = new AtomicLong();
        AtomicLong down = new AtomicLong();
        long started = System.currentTimeMillis();

        try {
            server = new Socket();
            server.connect(new InetSocketAddress(host, port), 10000);

            // Minecraft is latency-sensitive and its packets are small, so Nagle
            // batching here would add delay for no benefit.
            client.setTcpNoDelay(true);
            server.setTcpNoDelay(true);

            System.out.println("[+] " + who + " connected  (" + LIVE.incrementAndGet() + " live)");

            Socket s = server;
            Thread toServer = new Thread(() -> pump(client, s, up), "up");
            toServer.start();
            pump(server, client, down);           // this thread carries server -> client
            toServer.join(2000);

        } catch (Exception e) {
            System.out.println("[!] " + who + " failed: " + e.getMessage());
        } finally {
            close(client);
            close(server);
            long secs = (System.currentTimeMillis() - started) / 1000;
            System.out.println("[-] " + who + " closed after " + secs + "s"
                    + "  up " + kb(up.get()) + "  down " + kb(down.get())
                    + "  (" + LIVE.decrementAndGet() + " live)");
        }
    }

    /** Copies until either end closes, then shuts the far side down so the peer thread also ends. */
    private static void pump(Socket from, Socket to, AtomicLong counter) {
        byte[] buf = new byte[16 * 1024];
        try {
            InputStream in = from.getInputStream();
            OutputStream out = to.getOutputStream();
            int n;
            while ((n = in.read(buf)) != -1) {
                out.write(buf, 0, n);
                out.flush();
                counter.addAndGet(n);
            }
        } catch (IOException ignored) {
            // A player quitting is an abrupt close; that is normal, not an error.
        } finally {
            close(from);
            close(to);
        }
    }

    /** The real host and port behind a Minecraft SRV record, or null if there isn't one. */
    private static String[] lookupSrv(String host) {
        try {
            Hashtable<String, String> env = new Hashtable<>();
            env.put("java.naming.factory.initial", "com.sun.jndi.dns.DnsContextFactory");
            javax.naming.directory.DirContext ctx =
                    new javax.naming.directory.InitialDirContext(env);
            javax.naming.directory.Attributes attrs =
                    ctx.getAttributes("_minecraft._tcp." + host, new String[] { "SRV" });
            javax.naming.directory.Attribute srv = attrs.get("SRV");
            if (srv == null) return null;

            // "priority weight port target."
            String[] parts = srv.get(0).toString().trim().split("\\s+");
            if (parts.length < 4) return null;
            String target = parts[3].endsWith(".")
                    ? parts[3].substring(0, parts[3].length() - 1)
                    : parts[3];
            return new String[] { target, parts[2] };
        } catch (Throwable t) {
            return null;   // no SRV record, or no DNS provider -- fall back to 25565
        }
    }

    private static String kb(long bytes) {
        return bytes < 1024 ? bytes + "B" : (bytes / 1024) + "KB";
    }

    private static void close(Socket s) {
        if (s != null) try { s.close(); } catch (IOException ignored) { }
    }
}
