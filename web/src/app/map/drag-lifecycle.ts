/**
 * Pure state machine for the map's alt-drag "radius query" gesture.
 *
 * It knows nothing about Leaflet or the DOM — `MapShellComponent` wires mouse/keyboard events
 * to these methods. Keeping this logic pure (no timers, no Leaflet types) makes it possible to
 * unit-test the gesture's edge cases directly, instead of only through brittle jsdom + Leaflet
 * event simulation.
 */

export interface Point {
  lat: number;
  lng: number;
}

export interface DragReleaseResult {
  anchor: Point;
  radiusKm: number;
}

export type DistanceFn = (a: Point, b: Point) => number;

export class DragLifecycle {
  private _anchor: Point | null = null;
  private _previewRadiusKm = 0;
  private suppressClick = false;

  constructor(
    private readonly distanceKm: DistanceFn,
    private readonly minRadiusKm = 0.05,
  ) {}

  /** Whether an alt-drag is currently in progress. */
  get active(): boolean {
    return this._anchor !== null;
  }

  /** The anchor point of the in-progress drag, or null when idle. */
  get anchor(): Point | null {
    return this._anchor;
  }

  /** The live preview radius (km) while dragging; 0 when idle. */
  get previewRadiusKm(): number {
    return this._previewRadiusKm;
  }

  /** Starts a new drag at `anchor`. */
  begin(anchor: Point): void {
    this._anchor = anchor;
    this._previewRadiusKm = 0;
  }

  /**
   * Updates the live preview radius while dragging. Returns the new radius, or null if no drag
   * is active (caller should not render a preview in that case).
   */
  move(point: Point): number | null {
    if (!this._anchor) return null;
    this._previewRadiusKm = this.distanceKm(this._anchor, point);
    return this._previewRadiusKm;
  }

  /**
   * Ends the drag with a release *inside* the map container — the normal case.
   * Returns the query to run, or null if the drag never moved far enough to count as a
   * deliberate radius (a plain alt-click).
   *
   * Either way, marks the next click for suppression: Leaflet still fires a synthetic `click`
   * after this `mouseup`, and it must not re-query at the release point.
   */
  releaseInside(point: Point): DragReleaseResult | null {
    if (!this._anchor) return null;
    const anchor = this._anchor;
    const radiusKm = this.distanceKm(anchor, point);
    this.reset();
    this.suppressClick = true;
    return radiusKm > this.minRadiusKm ? { anchor, radiusKm } : null;
  }

  /**
   * Aborts the drag without querying: release outside the map container, the pointer leaving
   * the container mid-drag, or an explicit Escape. Escape only cancels the *drag* gesture — the
   * mouse button may still be held down, and releasing it afterwards (inside the container, or
   * back inside after a mouseout) still fires a native browser `click` there, since Leaflet's
   * own drag-suppression never engaged. Arming suppression here — exactly like `releaseInside` —
   * means that stray click is swallowed instead of re-querying at the release point. The caller
   * is responsible for scheduling `expireClickSuppression()` so an armed-but-unconsumed flag
   * (no click ever follows) self-clears.
   */
  cancel(): void {
    this.reset();
    this.suppressClick = true;
  }

  /** Called once per `click` event; returns true (and consumes the flag) exactly once per suppressed release. */
  consumeClickSuppression(): boolean {
    if (!this.suppressClick) return false;
    this.suppressClick = false;
    return true;
  }

  /** Safety net in case the expected click never arrives — timer-driven from the component. */
  expireClickSuppression(): void {
    this.suppressClick = false;
  }

  private reset(): void {
    this._anchor = null;
    this._previewRadiusKm = 0;
  }
}
