import { TestBed } from '@angular/core/testing';
import { describe, it, expect, vi } from 'vitest';
import { App } from './app';
import { GeoApiService } from './core/geo-api.service';

describe('App', () => {
  it('renders the shell with the gesture toast', async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        {
          provide: GeoApiService,
          useValue: { nearest: vi.fn(), within: vi.fn(), distance: vi.fn() },
        },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('nearest');
  });

  it('mounts the hud bar and query panel components', async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        {
          provide: GeoApiService,
          useValue: { nearest: vi.fn(), within: vi.fn(), distance: vi.fn() },
        },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('app-hud-bar')).toBeTruthy();
    expect(el.querySelector('app-query-panel')).toBeTruthy();
    // map-shell needs real DOM sizing for Leaflet; assert only that it exists.
    expect(el.querySelector('app-map-shell')).toBeTruthy();
  });
});
