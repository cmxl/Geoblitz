import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  effect,
  inject,
  viewChild,
  afterNextRender,
  DestroyRef,
} from '@angular/core';
import * as L from 'leaflet';
import { GeoQueryStore } from '../core/geo-query.store';
import {
  buildQueryPin,
  buildResultPin,
  buildRadiusCircle,
  buildRulerLine,
  distanceLabelHtml,
  RESULT_PIN_STYLE,
  RESULT_PIN_STYLE_HOT,
} from './layers';
import { DragLifecycle, Point } from './drag-lifecycle';

@Component({
  selector: 'app-map-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<div #host class="host"></div>`,
  styles: `
    :host,
    .host {
      display: block;
      position: absolute;
      inset: 0;
    }
    ::ng-deep .fd-distance-label {
      font: 600 13px/1 var(--fd-mono);
      color: var(--fd-amber);
      background: var(--fd-glass);
      border: 1px solid var(--fd-line);
      border-radius: 6px;
      padding: 4px 8px;
      white-space: nowrap;
    }
  `,
})
export class MapShellComponent {
  private readonly host = viewChild.required<ElementRef<HTMLDivElement>>('host');
  private readonly store = inject(GeoQueryStore);
  private readonly destroyRef = inject(DestroyRef);

  private map: L.Map | null = null;
  private layers = L.layerGroup();
  private dragPreview: L.Circle | null = null;
  /** Result pins from the last rebuild, indexed exactly like `store.results()` — lets the
   *  highlight effect below restyle one marker directly instead of rebuilding all of them. */
  private resultPins: L.CircleMarker[] = [];
  /** Previously-highlighted index, so the highlight effect can un-style it on change. */
  private lastHighlighted: number | null = null;
  private readonly dragLifecycle = new DragLifecycle(
    (a, b) => L.latLng(a.lat, a.lng).distanceTo(L.latLng(b.lat, b.lng)) / 1000,
  );
  private windowMouseUpHandler: ((e: MouseEvent) => void) | null = null;

  constructor() {
    afterNextRender(() => this.initMap());
    // Rebuild effect: reads queryPoint/results/mode/radiusKm/rulerPoints/distanceKm — it
    // deliberately never reads highlightedIndex, so hovering a pin/row does NOT trigger it.
    effect(() => this.render());
    // Highlight effect: reads ONLY highlightedIndex, so it re-runs on hover and nothing else.
    // It restyles at most two existing markers instead of clearing + rebuilding every layer.
    effect(() => this.applyHighlight());
  }

  private initMap(): void {
    const map = L.map(this.host().nativeElement, {
      zoomControl: false,
      attributionControl: true,
      // Shared canvas renderer: up to ~1000 result pins draw into one <canvas> element
      // instead of one SVG <path> node each. Tooltips and mouseover/mouseout hit detection
      // still work — Leaflet 1.9's canvas renderer implements its own hit testing.
      renderer: L.canvas(),
    }).setView([48.1374, 11.5755], 6);
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxZoom: 19,
      // Keep an extra ring of tiles loaded past the viewport edge (default 2) so panning
      // doesn't churn tile requests right as the map catches up to the cursor.
      keepBuffer: 4,
      attribution:
        '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
    }).addTo(map);
    L.control.zoom({ position: 'bottomleft' }).addTo(map);
    this.layers.addTo(map);

    map.on('click', (e) => {
      if (this.dragLifecycle.consumeClickSuppression()) return; // the alt-drag release already queried
      const { lat, lng } = e.latlng;
      switch (this.store.mode()) {
        case 'nearest':
          void this.store.queryNearest(lat, lng);
          break;
        case 'within':
          void this.store.queryWithin(lat, lng);
          break;
        case 'distance':
          void this.store.addRulerPoint(lat, lng);
          break;
      }
    });
    map.on('mousedown', (e) => {
      if (!e.originalEvent.altKey || this.dragLifecycle.active) return; // ignore a re-entrant
      // mousedown while a drag is already active — it would otherwise leak the previous
      // window-level mouseup listener registered below.
      e.originalEvent.preventDefault();
      map.dragging.disable();
      this.dragLifecycle.begin({ lat: e.latlng.lat, lng: e.latlng.lng });
      if (this.store.mode() !== 'within') this.store.setMode('within');
      this.attachDragEndListener(map);
    });
    map.on('mousemove', (e) => {
      const point: Point = { lat: e.latlng.lat, lng: e.latlng.lng };
      const radiusKm = this.dragLifecycle.move(point);
      if (radiusKm === null) return;
      const anchor = this.dragLifecycle.anchor!;
      this.dragPreview?.remove();
      this.dragPreview = buildRadiusCircle(anchor.lat, anchor.lng, radiusKm).addTo(map);
    });
    // Leaflet's map only listens for mouseup on its own container, so a release outside it
    // (or the pointer leaving mid-drag) would otherwise leave the gesture wedged — cancel it.
    map.on('mouseout', () => {
      if (!this.dragLifecycle.active) return;
      this.cancelDrag(map);
    });

    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && this.dragLifecycle.active) {
        this.cancelDrag(map);
        return;
      }
      if (e.key.toLowerCase() === 'd' && !(e.target instanceof HTMLInputElement)) {
        this.store.setMode(this.store.mode() === 'distance' ? 'nearest' : 'distance');
      }
    };
    window.addEventListener('keydown', onKey);
    this.destroyRef.onDestroy(() => {
      window.removeEventListener('keydown', onKey);
      this.detachDragEndListener();
      this.dragLifecycle.dispose();
      map.remove();
    });

    this.map = map;
    this.render();
  }

  /**
   * Registers a window-level `mouseup` listener for the duration of one alt-drag. Window-level
   * (rather than the map's own container-scoped `mouseup`) is what lets us detect a release
   * outside the map container — Leaflet's map never sees that event at all.
   */
  private attachDragEndListener(map: L.Map): void {
    this.detachDragEndListener(); // idempotent: never register two window listeners at once
    const handler = (e: MouseEvent) => {
      const rect = map.getContainer().getBoundingClientRect();
      const insideContainer =
        e.clientX >= rect.left &&
        e.clientX <= rect.right &&
        e.clientY >= rect.top &&
        e.clientY <= rect.bottom;
      // `active` can already be false here: an earlier Escape/mouseout may have cancelled the
      // drag while the button was still held down (see `cancelDrag`), in which case this mouseup
      // is just the delayed button-up and neither branch below applies.
      if (this.dragLifecycle.active) {
        if (insideContainer) {
          const latlng = map.mouseEventToLatLng(e);
          const result = this.dragLifecycle.releaseInside({ lat: latlng.lat, lng: latlng.lng });
          if (result)
            void this.store.queryWithin(result.anchor.lat, result.anchor.lng, result.radiusKm);
        } else {
          // Released outside the container: Leaflet's map never sees this event at all, so
          // nothing else aborts the drag for us. This cancel happens exactly at the real
          // mouseup (this handler), so — unlike Escape/mouseout — the expiry timer started
          // below is correctly bounding the release→click window from the start.
          this.dragLifecycle.cancel();
        }
      }
      // Reached on every real mouseup that ends a drag, whichever branch (if any) ran above:
      // a normal release, an out-of-window cancel, or the delayed button-up following an
      // earlier Escape/mouseout cancel. This is the one place the browser's synthetic click
      // is actually bounded from, so it's the single point that (re)starts the expiry timer.
      this.dragLifecycle.noteMouseUp();
      this.endDragVisuals(map);
    };
    this.windowMouseUpHandler = handler;
    window.addEventListener('mouseup', handler);
  }

  private detachDragEndListener(): void {
    if (this.windowMouseUpHandler) {
      window.removeEventListener('mouseup', this.windowMouseUpHandler);
      this.windowMouseUpHandler = null;
    }
  }

  /**
   * Aborts the in-progress drag from a pre-mouseup path: pointer leaving mid-drag (mouseout) or
   * an explicit Escape. The mouse button may still be held down at this point — Leaflet's own
   * drag-suppression never engaged, so a native `click` will still fire wherever it's eventually
   * released (see `DragLifecycle.cancel` doc comment). Suppression is armed now, but its expiry
   * timer deliberately is NOT started here and the window-level mouseup listener is deliberately
   * left attached: `attachDragEndListener`'s handler calls `noteMouseUp()` once the button is
   * actually released, which is what the timer must be bounded from. Only the visual/interaction
   * side effects (preview circle, map panning) are torn down immediately.
   */
  private cancelDrag(map: L.Map): void {
    this.dragLifecycle.cancel();
    this.clearDragVisuals(map);
  }

  /** Removes the preview circle and re-enables map panning; safe to call more than once. */
  private clearDragVisuals(map: L.Map): void {
    this.dragPreview?.remove();
    this.dragPreview = null;
    map.dragging.enable();
  }

  /** Full teardown once the drag has actually ended (its mouseup has been observed). */
  private endDragVisuals(map: L.Map): void {
    this.clearDragVisuals(map);
    this.detachDragEndListener();
  }

  /**
   * Redraws the single layer group from store state. Reads every store signal EXCEPT
   * `highlightedIndex` — that one is owned exclusively by `applyHighlight()` below, so this
   * effect does not re-run on hover. Result pins are always built unhighlighted; the
   * highlight effect restyles the right one in place right after (initial paint included).
   */
  private render(): void {
    const queryPoint = this.store.queryPoint();
    const results = this.store.results();
    const mode = this.store.mode();
    const radiusKm = this.store.radiusKm();
    const rulerPoints = this.store.rulerPoints();
    const distanceKm = this.store.distanceKm();
    if (!this.map) return;

    this.layers.clearLayers();
    if (queryPoint && mode !== 'distance') {
      this.layers.addLayer(buildQueryPin(queryPoint.lat, queryPoint.lon));
      if (mode === 'within')
        this.layers.addLayer(buildRadiusCircle(queryPoint.lat, queryPoint.lon, radiusKm));
    }
    this.resultPins = results.map((city, i) => {
      const pin = buildResultPin(city, i, false, (idx) => this.store.highlight(idx));
      this.layers.addLayer(pin);
      return pin;
    });
    if (mode === 'distance' && rulerPoints.length > 0) {
      rulerPoints.forEach((p) => this.layers.addLayer(buildQueryPin(p.lat, p.lon)));
      if (rulerPoints.length === 2) {
        this.layers.addLayer(buildRulerLine(rulerPoints));
        if (distanceKm !== null) {
          const mid: [number, number] = [
            (rulerPoints[0].lat + rulerPoints[1].lat) / 2,
            (rulerPoints[0].lon + rulerPoints[1].lon) / 2,
          ];
          this.layers.addLayer(
            L.marker(mid, {
              icon: L.divIcon({
                html: distanceLabelHtml(distanceKm),
                className: '',
                iconSize: undefined,
              }),
              interactive: false,
            }),
          );
        }
      }
    }
  }

  /**
   * Restyles just the previously- and newly-highlighted result pins via `setStyle`, using
   * the same `RESULT_PIN_STYLE`/`RESULT_PIN_STYLE_HOT` consts `buildResultPin` paints with
   * initially — no rebuild, no layer churn, so hovering 1000 pins/rows stays O(1) per hover
   * instead of O(n). Reads ONLY `highlightedIndex`, so it does not re-run when `render()`'s
   * signals change (and vice versa).
   */
  private applyHighlight(): void {
    const highlighted = this.store.highlightedIndex();
    const previous = this.lastHighlighted;
    this.lastHighlighted = highlighted;
    if (previous !== null) this.resultPins[previous]?.setStyle(RESULT_PIN_STYLE);
    if (highlighted !== null) this.resultPins[highlighted]?.setStyle(RESULT_PIN_STYLE_HOT);
  }
}
