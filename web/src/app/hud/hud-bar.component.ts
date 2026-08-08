import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { GeoQueryStore } from '../core/geo-query.store';
import { EngineTimePipe } from '../core/format.pipes';

@Component({
  selector: 'app-hud-bar',
  imports: [EngineTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="hud">
      <span class="brand">HIGH<em>PERF</em>/GEO</span>
      @if (store.linkDown()) {
        <span class="stat down">LINK DOWN</span>
      } @else if (store.lastTiming(); as t) {
        <span class="stat cyan"
          >engine
          @if (t.cacheHit) {
            <b [title]="t.engineMicros | engineTime">cached</b>
          } @else {
            <b>{{ t.engineMicros | engineTime }}</b>
          }
        </span>
        <span class="stat"
          >http <b>{{ t.httpMillis.toFixed(1) }} ms</b></span
        >
        <span class="stat" title="benchmark-suite claim — see docs/benchmarks.md"
          >alloc <b>0 B*</b></span
        >
        <span class="stat cyan"
          >cache <b>{{ t.cacheHit ? 'HIT' : 'MISS' }} · #{{ t.computeCount ?? '—' }}</b></span
        >
      } @else {
        <span class="stat">engine —</span>
        <span class="stat" title="benchmark-suite claim — see docs/benchmarks.md"
          >alloc <b>0 B*</b></span
        >
      }
      <svg
        class="spark"
        [attr.viewBox]="'0 0 ' + 40 * 5 + ' 16'"
        preserveAspectRatio="none"
        aria-hidden="true"
      >
        @for (bar of bars(); track bar.i) {
          <rect
            class="bar"
            [class.max]="bar.isMax"
            [attr.x]="bar.i * 5"
            [attr.y]="16 - bar.h"
            width="3"
            [attr.height]="bar.h"
          />
        }
      </svg>
    </div>
  `,
  styles: `
    .hud {
      height: 44px;
      display: flex;
      align-items: center;
      gap: 22px;
      padding: 0 18px;
      background: var(--fd-glass);
      backdrop-filter: blur(6px);
      border-bottom: 1px solid var(--fd-line);
      font: 12px/1 var(--fd-mono);
      color: var(--fd-muted);
    }
    .brand {
      color: var(--fd-text);
      font-weight: 600;
      letter-spacing: 0.1em;
    }
    .brand em {
      color: var(--fd-cyan);
      font-style: normal;
    }
    .stat b {
      color: var(--fd-amber);
      font-weight: 600;
    }
    .stat.cyan b {
      color: var(--fd-cyan);
    }
    .stat.down {
      color: var(--fd-red);
      font-weight: 600;
    }
    .spark {
      margin-left: auto;
      height: 16px;
      width: 200px;
    }
    .bar {
      fill: #2d5d78;
    }
    .bar.max {
      fill: var(--fd-amber);
    }
  `,
})
export class HudBarComponent {
  protected readonly store = inject(GeoQueryStore);

  protected readonly bars = computed(() => {
    const timings = this.store.timings();
    const max = Math.max(1, ...timings.map((t) => t.httpMillis));
    return timings.map((t, i) => ({
      i,
      h: Math.max(2, Math.round((t.httpMillis / max) * 16)),
      isMax: t.httpMillis === max && timings.length > 1,
    }));
  });
}
