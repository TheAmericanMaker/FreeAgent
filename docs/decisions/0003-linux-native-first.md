# 0003: Linux-native first

Status: Accepted

Linux workstations are the first-class FreeAgent target.

The initial implementation may rely on POSIX rename atomicity, fsync, process groups, Linux signal semantics, and `/bin/bash` or `/bin/sh`. Windows and macOS support are deferred, but should not be designed out of the architecture.
