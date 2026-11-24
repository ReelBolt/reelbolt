package agent

import (
	"reflect"
	"testing"
)

func TestRaiseFFmpegThreads(t *testing.T) {
	cases := []struct {
		name  string
		in    []string
		cores int
		want  []string
	}{
		{"raises 4 to 8", []string{"-threads", "4", "-i", "a.mp4", "o.mp4"}, 8, []string{"-threads", "8", "-i", "a.mp4", "o.mp4"}},
		{"keeps position of input and output flags", []string{"-threads", "4", "-i", "a", "-threads", "2", "o"}, 8, []string{"-threads", "8", "-i", "a", "-threads", "8", "o"}},
		{"one stays one", []string{"-threads", "1", "-i", "a"}, 8, []string{"-threads", "1", "-i", "a"}},
		{"zero is already auto", []string{"-threads", "0"}, 8, []string{"-threads", "0"}},
		{"never above the cores", []string{"-threads", "16"}, 8, []string{"-threads", "16"}},
		{"equal is left alone", []string{"-threads", "8"}, 8, []string{"-threads", "8"}},
		{"not a number", []string{"-threads", "auto"}, 8, []string{"-threads", "auto"}},
		{"no flag", []string{"-i", "a"}, 8, []string{"-i", "a"}},
		{"single core machine", []string{"-threads", "4"}, 1, []string{"-threads", "4"}},
		{"trailing flag without value", []string{"-i", "a", "-threads"}, 8, []string{"-i", "a", "-threads"}},
	}
	for _, c := range cases {
		in := append([]string(nil), c.in...)
		got := RaiseFFmpegThreads(c.in, c.cores)
		if !reflect.DeepEqual(got, c.want) {
			t.Errorf("%s: got %v want %v", c.name, got, c.want)
		}
		if !reflect.DeepEqual(c.in, in) {
			t.Errorf("%s: input was modified", c.name)
		}
	}
}

func TestRaiseRemotionConcurrency(t *testing.T) {
	cases := []struct {
		name string
		in   []string
		want []string
	}{
		{"equals form", []string{"remotion", "render", "--concurrency=2", "Main"}, []string{"remotion", "render", "--concurrency=8", "Main"}},
		{"two token form", []string{"render", "--concurrency", "3"}, []string{"render", "--concurrency", "8"}},
		{"out-of-memory retry keeps one", []string{"render", "--concurrency=1"}, []string{"render", "--concurrency=1"}},
		{"percentage untouched", []string{"render", "--concurrency=50%"}, []string{"render", "--concurrency=50%"}},
		{"above cores untouched", []string{"render", "--concurrency=12"}, []string{"render", "--concurrency=12"}},
		{"other flags untouched", []string{"render", "--concurrency-x=2"}, []string{"render", "--concurrency-x=2"}},
	}
	for _, c := range cases {
		if got := RaiseRemotionConcurrency(c.in, 8); !reflect.DeepEqual(got, c.want) {
			t.Errorf("%s: got %v want %v", c.name, got, c.want)
		}
	}
}
