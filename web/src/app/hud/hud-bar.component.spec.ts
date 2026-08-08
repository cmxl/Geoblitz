import { TestBed } from '@angular/core/testing';
import { describe, it, expect, beforeEach, vi } from 'vitest';
import { HudBarComponent } from './hud-bar.component';
import { GeoQueryStore } from '../core/geo-query.store';
import { GeoApiService } from '../core/geo-api.service';

describe('HudBarComponent', () => {
  let api: { nearest: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    api = { nearest: vi.fn() };
    TestBed.configureTestingModule({ providers: [{ provide: GeoApiService, useValue: api }] });
  });

  function render(): HTMLElement {
    const fixture = TestBed.createComponent(HudBarComponent);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows idle placeholders before any request', () => {
    const el = render();
    expect(el.textContent).toContain('engine —');
    expect(el.textContent).toContain('alloc 0 B');
  });

  it('shows engine time and MISS after a computed request', async () => {
    api.nearest.mockResolvedValue({
      data: { count: 0, cities: [] },
      engineMicros: 9.1,
      computeCount: 7,
      httpMillis: 1.53,
    });
    const store = TestBed.inject(GeoQueryStore);
    await store.queryNearest(1, 1);
    const el = render();
    expect(el.textContent).toContain('9.1 µs');
    expect(el.textContent).toContain('MISS · #7');
    expect(el.querySelectorAll('rect.bar').length).toBe(1);
  });

  it('labels cache hits as cached instead of the stale engine time', async () => {
    api.nearest
      .mockResolvedValueOnce({
        data: { count: 0, cities: [] },
        engineMicros: 9.1,
        computeCount: 7,
        httpMillis: 1.5,
      })
      .mockResolvedValueOnce({
        data: { count: 0, cities: [] },
        engineMicros: 9.1,
        computeCount: 7,
        httpMillis: 0.4,
      });
    const store = TestBed.inject(GeoQueryStore);
    await store.queryNearest(1, 1);
    await store.queryNearest(1, 1);
    const el = render();
    expect(el.textContent).toContain('cached');
    expect(el.textContent).toContain('HIT · #7');
    const cached = Array.from(el.querySelectorAll('b')).find((b) => b.textContent === 'cached');
    expect(cached?.getAttribute('title')).toBe('9.1 µs');
  });

  it('shows link-down state', async () => {
    api.nearest.mockRejectedValue(new TypeError('fetch failed'));
    const store = TestBed.inject(GeoQueryStore);
    await store.queryNearest(1, 1);
    const el = render();
    expect(el.textContent).toContain('LINK DOWN');
  });
});
