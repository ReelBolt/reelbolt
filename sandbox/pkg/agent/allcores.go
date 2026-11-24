package agent

import "strconv"

// All-cores mode (pool nodes only). A pool node runs one heavy job at a time on a machine
// that exists only for that job, so the job should use every core. The engine builds the
// commands with the thread counts it uses next to its own services (ffmpeg "-threads 4",
// Remotion "--concurrency=2"); a worker started with REELBOLT_WORKER_USE_ALL_CORES raises those
// numbers to the node's core count and changes nothing else.
//
// The rule is deliberately narrow: only an explicit integer of 2 or more that is below the core
// count is raised. 0 and "auto" already mean every core, 1 is how the engine (and the out-of-memory
// retry of a render) asks for one-at-a-time, and a value at or above the core count is left alone
// (Remotion refuses a concurrency above the core count). Anything that is not a plain integer, such
// as a percentage, is left alone too.

// RaiseFFmpegThreads returns argv with every "-threads N" value raised to cores per the rule
// above. argv is not modified; it is returned unchanged when nothing applies.
func RaiseFFmpegThreads(argv []string, cores int) []string {
	if cores < 2 {
		return argv
	}
	var out []string
	for i := 0; i+1 < len(argv); i++ {
		if argv[i] != "-threads" {
			continue
		}
		if v, ok := raisedValue(argv[i+1], cores); ok {
			if out == nil {
				out = append([]string(nil), argv...)
			}
			out[i+1] = v
		}
	}
	if out == nil {
		return argv
	}
	return out
}

// RaiseRemotionConcurrency does the same for "--concurrency=N" and "--concurrency N".
func RaiseRemotionConcurrency(args []string, cores int) []string {
	if cores < 2 {
		return args
	}
	const flag = "--concurrency"
	var out []string
	set := func(i int, v string) {
		if out == nil {
			out = append([]string(nil), args...)
		}
		out[i] = v
	}
	for i := 0; i < len(args); i++ {
		a := args[i]
		switch {
		case len(a) > len(flag)+1 && a[:len(flag)+1] == flag+"=":
			if v, ok := raisedValue(a[len(flag)+1:], cores); ok {
				set(i, flag+"="+v)
			}
		case a == flag && i+1 < len(args):
			if v, ok := raisedValue(args[i+1], cores); ok {
				set(i+1, v)
			}
		}
	}
	if out == nil {
		return args
	}
	return out
}

func raisedValue(raw string, cores int) (string, bool) {
	n, err := strconv.Atoi(raw)
	if err != nil || n < 2 || n >= cores {
		return "", false
	}
	return strconv.Itoa(cores), true
}
