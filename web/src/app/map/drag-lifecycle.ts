/**
 * Pure state machine for the map's alt-drag "radius query" gesture.
 *
 * It knows nothing about Leaflet or the DOM — `MapShellComponent` wires mouse/keyboard events
 * to these methods. Keeping this logic free of Leaflet/DOM types makes it possible to unit-test
 * the gesture's edge cases directly, instead of only through brittle jsdom + Leaflet event
 * simulation. It does own one `setTimeout` internally (the click-suppression expiry, see
 * `noteMouseUp`) — that's deliberate: the expiry's correctness is entirely about *when* it
 * starts relative to the drag's actual mouseup, so it needs to be testable with
 * `vi.useFakeTimers()` in the same place as the rest of this state machine.
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
  private expiryTimer: ReturnType<typeof setTimeout> | null = null;

  constructor(
    private readonly distanceKm: DistanceFn,
    private readonly minRadiusKm = 0.05,
    /** How long after the drag's real mouseup a still-armed suppression self-clears. */
    private readonly clickSuppressionTimeoutMs = 350,
    /**
     * Last-resort ceiling armed by `cancel()`, in case the real mouseup never arrives at all
     * (release outside the browser window / OS focus loss mid-drag) so `noteMouseUp()` never
     * gets called to start the tight window above. Deliberately loose — several seconds — since
     * it only ever matters in that no-mouseup-ever case; a real mouseup always supersedes it.
     */
    private readonly cancelFallbackTimeoutMs = 3000,
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
    // A brand-new gesture must never inherit a timer armed by whatever came before it — a prior
    // gesture's real-mouseup 350ms window, or a prior cancel's long fallback — otherwise it can
    // fire mid-way through THIS gesture and wipe out suppression this one goes on to arm for
    // itself (cross-gesture leakage; see the "cross-gesture timer safety" spec describe block).
    this.clearExpiryTimer();
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
    this.arm();
    return radiusKm > this.minRadiusKm ? { anchor, radiusKm } : null;
  }

  /**
   * Aborts the drag without querying: release outside the map container, the pointer leaving
   * the container mid-drag, or an explicit Escape. Escape only cancels the *drag* gesture — the
   * mouse button may still be held down, and releasing it afterwards (inside the container, or
   * back inside after a mouseout) still fires a native browser `click` there, since Leaflet's
   * own drag-suppression never engaged. Arming suppression here — exactly like `releaseInside` —
   * means that stray click is swallowed instead of re-querying at the release point.
   *
   * Does NOT start the *tight* 350ms expiry timer here: the button may still be held down for
   * an arbitrary amount of time after this cancel, and the browser's synthetic click only
   * follows the eventual mouseup. Starting the tight window here would let suppression expire
   * long before the click it exists to catch — the caller must call `noteMouseUp()` once that
   * real mouseup is observed, and that's what actually bounds the release→click window.
   *
   * It does arm a long last-resort *fallback* timer (`cancelFallbackTimeoutMs`), though: the
   * real mouseup might never arrive at all — released outside the browser window, OS focus lost
   * mid-drag — in which case `noteMouseUp()` never gets called and nothing would otherwise ever
   * clear this suppression, permanently eating the next legitimate click. `noteMouseUp()`
   * supersedes this fallback the moment a real mouseup does show up.
   */
  cancel(): void {
    this.reset();
    this.arm();
    this.scheduleExpiry(this.cancelFallbackTimeoutMs);
  }

  /** Called once per `click` event; returns true (and consumes the flag) exactly once per suppressed release. */
  consumeClickSuppression(): boolean {
    if (!this.suppressClick) return false;
    this.suppressClick = false;
    return true;
  }

  /** Safety net in case the expected click never arrives — timer-driven, via `noteMouseUp`/`cancel`. */
  expireClickSuppression(): void {
    this.suppressClick = false;
  }

  /**
   * Marks the point where the drag actually ended by mouseup — the single moment that bounds
   * the release→click window, regardless of which path got here: a normal release inside the
   * container, an out-of-window release (mouseup observed with the drag still active), or the
   * delayed mouseup that follows an earlier Escape/mouseout `cancel()`. (Not the same event for
   * a plain release-inside — the caller still calls that path's own method for the query result;
   * this call only ever governs the expiry timer.) (Re)starts the expiry timer from now — at the
   * tight `clickSuppressionTimeoutMs`, superseding any looser fallback `cancel()` may have armed
   * — so any suppression armed anywhere in `[[previous noteMouseUp or start], now]` gets exactly
   * that long from *this* mouseup to be consumed before it self-clears.
   */
  noteMouseUp(): void {
    this.scheduleExpiry(this.clickSuppressionTimeoutMs);
  }

  /** Cancels any pending expiry timer; call on component teardown to avoid a dangling timer. */
  dispose(): void {
    this.clearExpiryTimer();
  }

  /** Arms suppression fresh, clearing any timer inherited from an earlier, unrelated gesture. */
  private arm(): void {
    this.clearExpiryTimer();
    this.suppressClick = true;
  }

  /** (Re)schedules the expiry timer for `ms` from now, replacing whatever was previously pending. */
  private scheduleExpiry(ms: number): void {
    this.clearExpiryTimer();
    this.expiryTimer = setTimeout(() => {
      this.expiryTimer = null;
      this.expireClickSuppression();
    }, ms);
  }

  private clearExpiryTimer(): void {
    if (this.expiryTimer !== null) {
      clearTimeout(this.expiryTimer);
      this.expiryTimer = null;
    }
  }

  private reset(): void {
    this._anchor = null;
    this._previewRadiusKm = 0;
  }
}
