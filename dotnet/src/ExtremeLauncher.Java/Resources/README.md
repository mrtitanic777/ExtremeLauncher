# JavaCheck.jar

A three-line Java program that prints `os.arch`, `java.version` and `java.vendor` from a candidate
JVM's own `System.getProperty`. `JavaChecker` runs it to find out what a Java installation actually
is, rather than guessing from its path.

**Why a jar and not `java -XshowSettings:properties -version`.** The `-XshowSettings` output is a
human-readable diagnostic, not an interface: its format is undocumented, it goes to stderr, and it
varies between vendors and versions. A jar we compiled answers in a format we chose.

## Source

`libraries/javacheck/JavaCheck.java`, shared with the Qt launcher — one source of truth for what gets
probed.

## Rebuilding

Only needed when the `.java` changes. Requires a JDK; the `-source 8 -target 8` targeting is what lets
the jar run on every JVM the launcher supports, including the Java 8 that old modpacks need.

```sh
cd libraries/javacheck
javac -source 8 -target 8 JavaCheck.java
printf 'Main-Class: JavaCheck\n' > manifest.txt
jar cfm JavaCheck.jar manifest.txt JavaCheck.class
mv JavaCheck.jar ../../dotnet/src/ExtremeLauncher.Java/Resources/
```

The jar is checked in because building it would put a JDK on the critical path of every .NET build,
on every machine, to produce 1 KiB of bytecode that changes approximately never.
