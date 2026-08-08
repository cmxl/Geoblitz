import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { GeoQueryStore } from '../core/geo-query.store';
import { KmPipe } from '../core/format.pipes';
import { QueryMode } from '../core/models';

@Component({
  selector: 'app-query-panel',
  imports: [KmPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="fd-card section">
      <h3 class="fd-label">Query</h3>
      <div class="modes">
        @for (m of modes; track m.value) {
          <button
            type="button"
            class="mode"
            [class.on]="store.mode() === m.value"
            (click)="store.setMode(m.value)"
          >
            {{ m.label }}
          </button>
        }
      </div>
      @switch (store.mode()) {
        @case ('nearest') {
          <label class="field"
            ><span>count</span>
            <input
              type="number"
              min="1"
              max="100"
              [value]="store.count()"
              (change)="commitCount($event)"
          /></label>
        }
        @case ('within') {
          <label class="field"
            ><span>radius {{ store.radiusKm().toFixed(0) }} km</span>
            <input
              type="range"
              min="1"
              max="500"
              [value]="store.radiusKm()"
              (input)="store.setRadiusKm(toNumber($event))"
              (change)="commitRadius($event)"
          /></label>
          <label class="field"
            ><span>min population</span>
            <input
              type="number"
              min="0"
              [value]="store.minPopulation()"
              (change)="commitMinPopulation($event)"
          /></label>
        }
        @case ('distance') {
          <p class="hint">Click two points on the map.</p>
          @if (store.distanceKm() !== null) {
            <p class="distance">{{ store.distanceKm() | km }}</p>
          }
        }
      }
      @if (store.queryPoint(); as q) {
        <div class="field ro">
          <span>lat</span><b>{{ q.lat.toFixed(6) }}</b>
        </div>
        <div class="field ro">
          <span>lon</span><b>{{ q.lon.toFixed(6) }}</b>
        </div>
      }
      @if (store.error(); as err) {
        <p class="error">
          <span>{{ err }}</span>
          <button
            type="button"
            class="dismiss"
            aria-label="Dismiss error"
            (click)="store.clearError()"
          >
            ×
          </button>
        </p>
      }
    </div>

    @if (store.mode() !== 'distance') {
      <div class="fd-card section results">
        <h3 class="fd-label">Results · {{ store.results().length }}</h3>
        <div class="list">
          @for (city of store.results(); track $index) {
            <div
              class="row"
              [class.hot]="store.highlightedIndex() === $index"
              (mouseenter)="store.highlight($index)"
              (mouseleave)="store.highlight(null)"
            >
              <span class="rank">{{ ($index + 1).toString().padStart(2, '0') }}</span>
              <span class="name">{{ city.name }}</span>
              <span class="cc">{{ city.country }}</span>
              <span class="km">{{ city.distanceKm | km }}</span>
            </div>
          }
        </div>
      </div>
    }
  `,
  styles: `
    :host {
      /* Let the two cards below participate directly as flex items of .slot-panel (app.ts) so
         the results card's \`flex: 1\` actually fills remaining height and its list scrolls
         internally instead of growing past the panel and covering the map's zoom control. */
      display: contents;
    }
    .section {
      padding: 14px 16px;
    }
    .fd-label {
      margin: 0 0 10px;
    }
    .modes {
      display: flex;
      gap: 6px;
      margin-bottom: 10px;
    }
    .mode {
      flex: 1;
      padding: 8px 0;
      font: 600 11px/1 var(--fd-mono);
      text-transform: uppercase;
      background: transparent;
      color: var(--fd-muted);
      border: 1px solid var(--fd-line);
      border-radius: 6px;
      cursor: pointer;
    }
    .mode.on {
      background: rgba(84, 215, 255, 0.14);
      color: var(--fd-cyan);
      border-color: rgba(84, 215, 255, 0.5);
    }
    .field {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 10px;
      font: 12px/1.9 var(--fd-mono);
      color: var(--fd-muted);
    }
    .field input {
      background: rgba(84, 215, 255, 0.06);
      color: var(--fd-text);
      border: 1px solid var(--fd-line);
      border-radius: 4px;
      padding: 4px 8px;
      font: 12px var(--fd-mono);
      width: 110px;
    }
    .field input[type='range'] {
      width: 140px;
      accent-color: var(--fd-cyan);
    }
    .field.ro b {
      color: var(--fd-text);
      font-weight: 500;
    }
    .hint {
      color: var(--fd-muted);
      font-size: 12.5px;
      margin: 4px 0;
    }
    .distance {
      font: 600 22px/1.2 var(--fd-mono);
      color: var(--fd-amber);
      margin: 6px 0 2px;
    }
    .error {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: 8px;
      color: var(--fd-red);
      font: 12px/1.5 var(--fd-mono);
      margin: 8px 0 0;
    }
    .dismiss {
      flex: none;
      background: transparent;
      border: none;
      color: var(--fd-red);
      font: 14px/1 var(--fd-mono);
      cursor: pointer;
      padding: 0 2px;
    }
    .results {
      flex: 1;
      min-height: 0;
      display: flex;
      flex-direction: column;
    }
    .list {
      overflow-y: auto;
    }
    .row {
      display: flex;
      align-items: baseline;
      gap: 8px;
      padding: 8px 2px;
      border-bottom: 1px solid rgba(94, 200, 255, 0.08);
      font-size: 13.5px;
      cursor: default;
      /* Row height is 8px + 8px padding + ~21px line box (13.5px font, browser default
         line-height ~1.55) ≈ 37px. With up to 1000 rows in .list, content-visibility: auto
         lets the browser skip layout/paint/style work entirely for rows scrolled out of view;
         contain-intrinsic-size gives it a placeholder size so scrollbar height stays correct
         before an offscreen row has ever been measured. */
      content-visibility: auto;
      contain-intrinsic-size: auto 37px;
    }
    .row.hot {
      background: rgba(84, 215, 255, 0.08);
    }
    .rank {
      font: 600 10px/1 var(--fd-mono);
      color: var(--fd-cyan);
      width: 18px;
    }
    .name {
      color: var(--fd-text);
      font-weight: 600;
    }
    .cc {
      font: 600 9px/1 var(--fd-mono);
      color: var(--fd-muted);
      border: 1px solid var(--fd-line);
      padding: 2px 4px;
      border-radius: 3px;
    }
    .km {
      margin-left: auto;
      font: 12.5px var(--fd-mono);
      color: var(--fd-amber);
    }
  `,
})
export class QueryPanelComponent {
  protected readonly store = inject(GeoQueryStore);
  protected readonly modes: { value: QueryMode; label: string }[] = [
    { value: 'nearest', label: 'Nearest' },
    { value: 'within', label: 'Within' },
    { value: 'distance', label: 'Dist' },
  ];

  protected toNumber(event: Event): number {
    return Number((event.target as HTMLInputElement).value);
  }

  /* Commit handlers: apply the (clamped) value, sync the input box to the clamped result,
     and re-run the active query so parameter changes take effect immediately instead of
     waiting for the next map click. The slider commits on release ('change'), not while
     dragging ('input' only updates the live label). */

  protected commitCount(event: Event): void {
    this.store.setCount(this.toNumber(event));
    (event.target as HTMLInputElement).value = String(this.store.count());
    void this.store.refresh();
  }

  protected commitRadius(event: Event): void {
    this.store.setRadiusKm(this.toNumber(event));
    void this.store.refresh();
  }

  protected commitMinPopulation(event: Event): void {
    this.store.setMinPopulation(this.toNumber(event));
    (event.target as HTMLInputElement).value = String(this.store.minPopulation());
    void this.store.refresh();
  }
}
