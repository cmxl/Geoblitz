import { describe, it, expect, vi } from 'vitest';
import { DragLifecycle, Point } from './drag-lifecycle';

/** Simple flat-earth distance stand-in — good enough to test the state machine, not geodesy. */
function planarKm(a: Point, b: Point): number {
  return Math.hypot(a.lat - b.lat, a.lng - b.lng);
}

describe('DragLifecycle', () => {
  it('is inactive until begin() is called', () => {
    const drag = new DragLifecycle(planarKm);
    expect(drag.active).toBe(false);
    expect(drag.anchor).toBeNull();
    expect(drag.previewRadiusKm).toBe(0);
  });

  it('begin() activates the drag and records the anchor', () => {
    const drag = new DragLifecycle(planarKm);
    drag.begin({ lat: 48, lng: 11 });
    expect(drag.active).toBe(true);
    expect(drag.anchor).toEqual({ lat: 48, lng: 11 });
    expect(drag.previewRadiusKm).toBe(0);
  });

  it('move() updates the live preview radius from the anchor', () => {
    const drag = new DragLifecycle(planarKm);
    drag.begin({ lat: 0, lng: 0 });
    const r1 = drag.move({ lat: 3, lng: 4 });
    expect(r1).toBe(5);
    expect(drag.previewRadiusKm).toBe(5);
    const r2 = drag.move({ lat: 6, lng: 8 });
    expect(r2).toBe(10);
  });

  it('move() is a no-op and returns null when no drag is active', () => {
    const drag = new DragLifecycle(planarKm);
    expect(drag.move({ lat: 1, lng: 1 })).toBeNull();
    expect(drag.previewRadiusKm).toBe(0);
  });

  describe('releaseInside', () => {
    it('returns the anchor and radius for a deliberate drag', () => {
      const drag = new DragLifecycle(planarKm, 0.05);
      drag.begin({ lat: 0, lng: 0 });
      drag.move({ lat: 3, lng: 4 });
      const result = drag.releaseInside({ lat: 3, lng: 4 });
      expect(result).toEqual({ anchor: { lat: 0, lng: 0 }, radiusKm: 5 });
    });

    it('clears the drag state so a subsequent release/move is a no-op', () => {
      const drag = new DragLifecycle(planarKm);
      drag.begin({ lat: 0, lng: 0 });
      drag.releaseInside({ lat: 3, lng: 4 });
      expect(drag.active).toBe(false);
      expect(drag.anchor).toBeNull();
      expect(drag.releaseInside({ lat: 1, lng: 1 })).toBeNull();
    });

    it('returns null (no query) when the movement never exceeds the minimum radius — a plain alt-click', () => {
      const drag = new DragLifecycle(planarKm, 0.05);
      drag.begin({ lat: 0, lng: 0 });
      const result = drag.releaseInside({ lat: 0.00001, lng: 0.00001 });
      expect(result).toBeNull();
    });

    it('returns null when no drag is active', () => {
      const drag = new DragLifecycle(planarKm);
      expect(drag.releaseInside({ lat: 1, lng: 1 })).toBeNull();
    });

    it('arms click suppression on a deliberate release', () => {
      const drag = new DragLifecycle(planarKm, 0.05);
      drag.begin({ lat: 0, lng: 0 });
      drag.releaseInside({ lat: 3, lng: 4 });
      expect(drag.consumeClickSuppression()).toBe(true);
    });

    it('arms click suppression even when the release radius is below the minimum', () => {
      const drag = new DragLifecycle(planarKm, 0.05);
      drag.begin({ lat: 0, lng: 0 });
      drag.releaseInside({ lat: 0.00001, lng: 0.00001 });
      expect(drag.consumeClickSuppression()).toBe(true);
    });
  });

  describe('cancel (release outside container / mouseleave / Escape)', () => {
    it('clears the drag state and arms click suppression', () => {
      const drag = new DragLifecycle(planarKm);
      drag.begin({ lat: 0, lng: 0 });
      drag.move({ lat: 3, lng: 4 });
      drag.cancel();
      expect(drag.active).toBe(false);
      expect(drag.anchor).toBeNull();
      expect(drag.previewRadiusKm).toBe(0);
      expect(drag.consumeClickSuppression()).toBe(true);
    });

    it('is a safe no-op when no drag is active, beyond arming suppression', () => {
      const drag = new DragLifecycle(planarKm);
      expect(() => drag.cancel()).not.toThrow();
      expect(drag.active).toBe(false);
    });

    it('leaves a subsequent releaseInside as a no-op (release-outside cannot combine with a later click)', () => {
      const drag = new DragLifecycle(planarKm);
      drag.begin({ lat: 0, lng: 0 });
      drag.cancel(); // e.g. released outside the container
      expect(drag.releaseInside({ lat: 9, lng: 9 })).toBeNull();
    });

    it('suppresses the synthetic click that follows Escape/mouseout-cancel when the button is released inside afterwards', () => {
      // Escape (or a mouseout) only cancels the drag gesture, not the held-down mouse button.
      // Releasing it back inside the container still fires a native `click` there — this must
      // be swallowed, not treated as a fresh nearest/within query at the release point.
      const drag = new DragLifecycle(planarKm);
      drag.begin({ lat: 48, lng: 11 });
      drag.move({ lat: 48.1, lng: 11.1 });
      drag.cancel();
      // No releaseInside call happens on this path in the real component (the window mouseup
      // listener is already detached by the time the button comes back up) — the stray event is
      // a plain browser `click`, handled solely via consumeClickSuppression().
      expect(drag.consumeClickSuppression()).toBe(true);
      expect(drag.consumeClickSuppression()).toBe(false); // consumed exactly once
    });

    it('cancel-then-releaseInside does not double-arm or leak suppression beyond a single consume', () => {
      const drag = new DragLifecycle(planarKm, 0.05);
      drag.begin({ lat: 0, lng: 0 });
      drag.cancel();
      expect(drag.releaseInside({ lat: 9, lng: 9 })).toBeNull(); // drag already inactive
      expect(drag.consumeClickSuppression()).toBe(true);
      expect(drag.consumeClickSuppression()).toBe(false);
    });
  });

  describe('click suppression', () => {
    it('consumes the flag exactly once', () => {
      const drag = new DragLifecycle(planarKm, 0.05);
      drag.begin({ lat: 0, lng: 0 });
      drag.releaseInside({ lat: 3, lng: 4 });
      expect(drag.consumeClickSuppression()).toBe(true);
      expect(drag.consumeClickSuppression()).toBe(false);
    });

    it('does not suppress clicks when no drag ever happened', () => {
      const drag = new DragLifecycle(planarKm);
      expect(drag.consumeClickSuppression()).toBe(false);
    });

    it('expireClickSuppression clears an armed-but-unconsumed flag (safety-timeout path)', () => {
      const drag = new DragLifecycle(planarKm, 0.05);
      drag.begin({ lat: 0, lng: 0 });
      drag.releaseInside({ lat: 3, lng: 4 });
      drag.expireClickSuppression();
      expect(drag.consumeClickSuppression()).toBe(false);
    });

    it('expireClickSuppression is a safe no-op when nothing is armed', () => {
      const drag = new DragLifecycle(planarKm);
      expect(() => drag.expireClickSuppression()).not.toThrow();
    });
  });

  it('distanceKm is invoked with the anchor as first argument and the moving point as second', () => {
    const distanceKm = vi.fn(planarKm);
    const drag = new DragLifecycle(distanceKm);
    const anchor = { lat: 1, lng: 2 };
    drag.begin(anchor);
    drag.move({ lat: 3, lng: 4 });
    expect(distanceKm).toHaveBeenCalledWith(anchor, { lat: 3, lng: 4 });
  });

  describe('noteMouseUp (350ms release->click expiry window)', () => {
    it('REGRESSION: a cancel followed by a long-delayed mouseup keeps suppression armed — the expiry timer must start at the mouseup, not at cancel', () => {
      // Escape/mouseout can cancel the drag while the mouse button is still held down. The
      // browser's synthetic click only fires once the button actually comes up, which may be
      // long after the cancel. If the expiry timer started at cancel time, it would have
      // expired suppression before the click that it exists to catch.
      vi.useFakeTimers();
      try {
        const drag = new DragLifecycle(planarKm);
        drag.begin({ lat: 48, lng: 11 });
        drag.cancel(); // e.g. Escape while the button is still held down
        vi.advanceTimersByTime(500); // way past 350ms, button still not released
        drag.noteMouseUp(); // the button is released only now
        expect(drag.consumeClickSuppression()).toBe(true);
      } finally {
        vi.useRealTimers();
      }
    });

    it('expires suppression 350ms after noteMouseUp, bounding the window from the real release', () => {
      vi.useFakeTimers();
      try {
        const drag = new DragLifecycle(planarKm);
        drag.begin({ lat: 48, lng: 11 });
        drag.cancel();
        drag.noteMouseUp();
        vi.advanceTimersByTime(350);
        expect(drag.consumeClickSuppression()).toBe(false);
      } finally {
        vi.useRealTimers();
      }
    });

    it('CHANGED (was: cancel never schedules any expiry — superseded by the MAJOR fallback fix below): cancel() does not clear suppression within the tight 350ms window — only the long last-resort fallback, or a later real mouseup, bounds it', () => {
      // Originally this asserted suppression stayed armed even after 10s with no mouseup ever
      // arriving — i.e. cancel() alone never self-clears. Code review flagged that as a MAJOR
      // bug: if the real mouseup never reaches the window listener at all (release outside the
      // browser window / OS focus loss), suppression stayed armed forever and ate the next
      // legitimate click. `cancel()` now arms a long last-resort fallback timer, so this test is
      // narrowed to prove only that the *tight* 350ms window doesn't govern a bare cancel() —
      // see the "cancel() with no mouseup ever arriving" test below for the fallback itself.
      vi.useFakeTimers();
      try {
        const drag = new DragLifecycle(planarKm);
        drag.begin({ lat: 48, lng: 11 });
        drag.cancel();
        vi.advanceTimersByTime(1000); // past the tight 350ms window, still well under the fallback
        expect(drag.consumeClickSuppression()).toBe(true);
      } finally {
        vi.useRealTimers();
      }
    });

    it('releaseInside + noteMouseUp still expires suppression 350ms after the release (unchanged real-release timing)', () => {
      vi.useFakeTimers();
      try {
        const drag = new DragLifecycle(planarKm, 0.05);
        drag.begin({ lat: 0, lng: 0 });
        drag.releaseInside({ lat: 3, lng: 4 });
        drag.noteMouseUp();
        vi.advanceTimersByTime(350);
        expect(drag.consumeClickSuppression()).toBe(false);
      } finally {
        vi.useRealTimers();
      }
    });

    it('dispose() clears a pending expiry timer so it cannot fire later and clobber a subsequent drag', () => {
      vi.useFakeTimers();
      try {
        const drag = new DragLifecycle(planarKm);
        drag.begin({ lat: 48, lng: 11 });
        drag.cancel();
        drag.noteMouseUp();
        drag.dispose();
        // If the timer were still pending, advancing past 350ms would call
        // expireClickSuppression() — arm a fresh cycle first so that would be observable.
        drag.begin({ lat: 0, lng: 0 });
        drag.cancel();
        vi.advanceTimersByTime(350);
        expect(drag.consumeClickSuppression()).toBe(true);
      } finally {
        vi.useRealTimers();
      }
    });
  });

  describe('cross-gesture timer safety (code review follow-up)', () => {
    it("REGRESSION: a timer armed by an earlier finished gesture cannot fire mid-way through a new gesture and clear the new gesture's suppression", () => {
      // Repro from review: drag A ends out-of-container (noteMouseUp arms a 350ms timer T).
      // Drag B begins and is Escape-cancelled before T elapses and before B's own mouseup. If
      // nothing clears T when B starts/cancels, T fires mid-way through B's gesture and wipes
      // out the suppression B just armed for itself, letting B's trailing click through.
      vi.useFakeTimers();
      try {
        const drag = new DragLifecycle(planarKm);
        // Gesture A: begins, ends out-of-container — its mouseup already happened, so its
        // suppression is bound by the tight 350ms window starting now (t=0).
        drag.begin({ lat: 0, lng: 0 });
        drag.cancel();
        drag.noteMouseUp(); // T scheduled to fire at t=350
        vi.advanceTimersByTime(50); // t=50

        // Gesture B: begins before T elapses...
        drag.begin({ lat: 1, lng: 1 });
        vi.advanceTimersByTime(50); // t=100

        // ...and is cancelled (e.g. Escape) before its own mouseup and before T elapses.
        drag.cancel();

        // Advance past t=350, when T (armed by A, not B) would have fired.
        vi.advanceTimersByTime(250); // t=350

        // B's own suppression must still be armed — A's stale timer must not have cleared it.
        expect(drag.consumeClickSuppression()).toBe(true);
      } finally {
        vi.useRealTimers();
      }
    });

    it('cancel() arms a long last-resort fallback so suppression cannot stay armed forever when the real mouseup never arrives at all', () => {
      // e.g. Escape while dragging, then the button is released outside the browser window
      // entirely (OS focus loss) — the window-level mouseup listener never sees it, so
      // noteMouseUp() is never called for this gesture. Without a ceiling, suppression would
      // stay armed forever and eat the next legitimate click.
      vi.useFakeTimers();
      try {
        const drag = new DragLifecycle(planarKm);
        drag.begin({ lat: 48, lng: 11 });
        drag.cancel();
        vi.advanceTimersByTime(10_000); // well past any reasonable release->click gap
        expect(drag.consumeClickSuppression()).toBe(false);
      } finally {
        vi.useRealTimers();
      }
    });

    it('a real noteMouseUp before the fallback fires supersedes it with the tight 350ms window', () => {
      vi.useFakeTimers();
      try {
        const drag = new DragLifecycle(planarKm);
        drag.begin({ lat: 48, lng: 11 });
        drag.cancel(); // arms the long fallback (several seconds)
        vi.advanceTimersByTime(500); // still well under the fallback
        drag.noteMouseUp(); // the real mouseup finally arrives — supersedes the fallback
        vi.advanceTimersByTime(350); // if the multi-second fallback were still governing, this
        // would still be armed; only the fresh, superseding 350ms window explains a clear here.
        expect(drag.consumeClickSuppression()).toBe(false);
      } finally {
        vi.useRealTimers();
      }
    });
  });

  it('a full begin -> move -> releaseInside -> begin cycle behaves independently each time', () => {
    const drag = new DragLifecycle(planarKm, 0.05);
    drag.begin({ lat: 0, lng: 0 });
    drag.move({ lat: 3, lng: 4 });
    expect(drag.releaseInside({ lat: 3, lng: 4 })).toEqual({
      anchor: { lat: 0, lng: 0 },
      radiusKm: 5,
    });
    drag.consumeClickSuppression();

    drag.begin({ lat: 10, lng: 10 });
    expect(drag.active).toBe(true);
    expect(drag.releaseInside({ lat: 10, lng: 16 })).toEqual({
      anchor: { lat: 10, lng: 10 },
      radiusKm: 6,
    });
  });
});
