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
} from './layers';

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
  private dragAnchor: L.LatLng | null = null;
  private dragPreview: L.Circle | null = null;

  constructor() {
    afterNextRender(() => this.initMap());
    effect(() => this.render()); // re-runs on any store signal read inside render()
  }

  private initMap(): void {
    const map = L.map(this.host().nativeElement, {
      zoomControl: false,
      attributionControl: true,
    }).setView([48.1374, 11.5755], 6);
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxZoom: 19,
      attribution:
        '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
    }).addTo(map);
    L.control.zoom({ position: 'bottomleft' }).addTo(map);
    this.layers.addTo(map);

    map.on('click', (e) => {
      if (this.dragAnchor) return; // alt-drag release handled on mouseup
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
      if (!e.originalEvent.altKey) return;
      e.originalEvent.preventDefault();
      map.dragging.disable();
      this.dragAnchor = e.latlng;
      if (this.store.mode() !== 'within') this.store.setMode('within');
    });
    map.on('mousemove', (e) => {
      if (!this.dragAnchor) return;
      const radiusM = this.dragAnchor.distanceTo(e.latlng);
      this.dragPreview?.remove();
      this.dragPreview = buildRadiusCircle(
        this.dragAnchor.lat,
        this.dragAnchor.lng,
        radiusM / 1000,
      ).addTo(map);
    });
    map.on('mouseup', (e) => {
      if (!this.dragAnchor) return;
      const anchor = this.dragAnchor;
      const radiusKm = anchor.distanceTo(e.latlng) / 1000;
      this.dragAnchor = null;
      this.dragPreview?.remove();
      this.dragPreview = null;
      map.dragging.enable();
      if (radiusKm > 0.05) void this.store.queryWithin(anchor.lat, anchor.lng, radiusKm);
    });

    const onKey = (e: KeyboardEvent) => {
      if (e.key.toLowerCase() === 'd' && !(e.target instanceof HTMLInputElement)) {
        this.store.setMode(this.store.mode() === 'distance' ? 'nearest' : 'distance');
      }
    };
    window.addEventListener('keydown', onKey);
    this.destroyRef.onDestroy(() => {
      window.removeEventListener('keydown', onKey);
      map.remove();
    });

    this.map = map;
    this.render();
  }

  /** Redraws the single layer group from store state. Reads signals → effect dependency. */
  private render(): void {
    const queryPoint = this.store.queryPoint();
    const results = this.store.results();
    const highlighted = this.store.highlightedIndex();
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
    results.forEach((city, i) =>
      this.layers.addLayer(
        buildResultPin(city, i, highlighted === i, (idx) => this.store.highlight(idx)),
      ),
    );
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
}
