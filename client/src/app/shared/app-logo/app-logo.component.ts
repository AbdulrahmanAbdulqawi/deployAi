import { Component, computed, inject, input } from '@angular/core';
import { appLogoAssetUrl, isAppLogoId } from '../../core/constants/app-logos';
import { ThemeService } from '../../core/services/theme.service';

@Component({
  selector: 'app-app-logo',
  standalone: true,
  templateUrl: './app-logo.component.html',
  styleUrl: './app-logo.component.scss',
  host: {
    class: 'app-logo-host',
    '[class.app-logo-host--orb]': 'variant() === "orb"',
    '[class.app-logo-host--picker]': 'variant() === "picker"',
  },
})
export class AppLogoComponent {
  private readonly theme = inject(ThemeService);

  readonly logoKey = input<string | null | undefined>();
  readonly variant = input<'orb' | 'picker'>('orb');
  readonly styleOverride = input<'default' | 'notebook' | undefined>();

  readonly src = computed(() => {
    const key = this.logoKey();
    if (!isAppLogoId(key)) {
      return null;
    }

    const style = this.styleOverride() ?? this.theme.style();
    return appLogoAssetUrl(key, style);
  });
}
