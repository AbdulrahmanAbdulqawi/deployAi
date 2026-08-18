import { Component, computed, input } from '@angular/core';
import { appLogoAssetUrl, isAppLogoId } from '../../core/constants/app-logos';

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
  readonly logoKey = input<string | null | undefined>();
  readonly variant = input<'orb' | 'picker'>('orb');

  readonly src = computed(() => {
    const key = this.logoKey();
    if (!isAppLogoId(key)) {
      return null;
    }

    return appLogoAssetUrl(key);
  });
}
