import { ChangeDetectionStrategy, Component } from '@angular/core';
import { MapShellComponent } from './map/map-shell.component';
import { HudBarComponent } from './hud/hud-bar.component';
import { QueryPanelComponent } from './panel/query-panel.component';

@Component({
  selector: 'app-root',
  imports: [MapShellComponent, HudBarComponent, QueryPanelComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="shell">
      <div class="slot-map"><app-map-shell /></div>
      <div class="slot-hud"><app-hud-bar /></div>
      <aside class="slot-panel"><app-query-panel /></aside>
      <div class="toast fd-card">
        click → nearest · <b>alt-drag</b> → radius · <b>D</b> + two clicks → distance
      </div>
    </div>
  `,
  styles: `
    .shell {
      position: fixed;
      inset: 0;
    }
    .slot-map {
      position: absolute;
      inset: 0;
    }
    .slot-hud {
      position: absolute;
      top: 0;
      left: 0;
      right: 0;
      height: 44px;
      z-index: 1000;
    }
    .slot-panel {
      position: absolute;
      top: 56px;
      left: 14px;
      bottom: 14px;
      width: 296px;
      z-index: 1000;
      display: flex;
      flex-direction: column;
      gap: 12px;
    }
    .toast {
      position: absolute;
      right: 14px;
      bottom: 14px;
      z-index: 1000;
      padding: 10px 14px;
      font: 11px/1.6 var(--fd-mono);
      color: var(--fd-muted);
    }
    .toast b {
      color: var(--fd-cyan);
      font-weight: 600;
    }
  `,
})
export class App {}
