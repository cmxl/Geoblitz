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
    it('clears the drag state without arming click suppression', () => {
      const drag = new DragLifecycle(planarKm);
      drag.begin({ lat: 0, lng: 0 });
      drag.move({ lat: 3, lng: 4 });
      drag.cancel();
      expect(drag.active).toBe(false);
      expect(drag.anchor).toBeNull();
      expect(drag.previewRadiusKm).toBe(0);
      expect(drag.consumeClickSuppression()).toBe(false);
    });

    it('is a safe no-op when no drag is active', () => {
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
