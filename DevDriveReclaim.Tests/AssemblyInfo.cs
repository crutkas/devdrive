// Tests create real temp trees and one test mutates a process-wide environment variable, so they
// must not run concurrently with each other.
[assembly: Parallelize(Scope = ExecutionScope.MethodLevel, Workers = 1)]
