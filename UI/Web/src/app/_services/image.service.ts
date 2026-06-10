import {computed, DestroyRef, inject, Injectable, signal} from '@angular/core';
import {environment} from 'src/environments/environment';
import {ThemeService} from './theme.service';
import {AccountService} from './account.service';
import {takeUntilDestroyed} from "@angular/core/rxjs-interop";
import {map} from "rxjs";
import {HttpClient} from "@angular/common/http";
import {CoverImageOption} from "./cover-chooser-config-factory.service";
import {translate} from "@jsverse/transloco";
import {ExternalCoverImageType, ExternalCoverResponse} from "../_models/kavitaplus/external-cover-response";
import {EVENTS, MessageHubService} from "./message-hub.service";

@Injectable({
  providedIn: 'root'
})
export class ImageService {
  private readonly accountService = inject(AccountService);
  private readonly themeService = inject(ThemeService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly httpClient = inject(HttpClient);
  private readonly hubService = inject(MessageHubService);

  private readonly baseUrl = environment.apiUrl;
  private readonly coverCacheBuster = signal(Date.now());
  apiKey = this.accountService.currentUserImageAuthKey;
  encodedKey = computed(() => encodeURIComponent(this.apiKey()!));
  private readonly coverAuthParams = computed(() => `apiKey=${this.encodedKey()}&v=${this.coverCacheBuster()}`);

  public placeholderImage = 'assets/images/image-placeholder.dark-min.png';
  public errorImage = 'assets/images/error-placeholder2.dark-min.png';
  public errorWebLinkImage = 'assets/images/broken-white-32x32.png';
  public noPersonImage = 'assets/images/error-person-missing.dark.min.png';

  constructor() {
    this.themeService.currentTheme$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(theme => {
      if (this.themeService.isDarkTheme()) {
        this.placeholderImage = 'assets/images/image-placeholder.dark-min.png';
        this.errorImage = 'assets/images/error-placeholder2.dark-min.png';
        this.errorWebLinkImage = 'assets/images/broken-white-32x32.png';
        this.noPersonImage = 'assets/images/error-person-missing.dark.min.png';
      } else {
        this.placeholderImage = 'assets/images/image-placeholder-min.png';
        this.errorImage = 'assets/images/error-placeholder2-min.png';
        this.errorWebLinkImage = 'assets/images/broken-black-32x32.png';
        this.noPersonImage = 'assets/images/error-person-missing.min.png';
      }
    });

    this.hubService.messages$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(message => {
      if (message.event === EVENTS.CoverUpdate) {
        this.coverCacheBuster.set(Date.now());
      }
    });
  }

  /**
   * Returns the entity type from a cover image url. Undefied if not applicable
   * @param url
   * @returns
   */
  getEntityTypeFromUrl(url: string) {
    if (url.indexOf('?') < 0) return undefined;
    const part = url.split('?')[1];
    const equalIndex = part.indexOf('=');
    return part.substring(0, equalIndex).replace('Id', '');
  }

  getPersonImage(personId: number) {
    return `${this.baseUrl}image/person-cover?personId=${personId}&${this.coverAuthParams()}`;
  }

  getUserCoverImage(userId: number) {
    return `${this.baseUrl}image/user-cover?userId=${userId}&${this.coverAuthParams()}`;
  }

  getLibraryCoverImage(libraryId: number) {
    return `${this.baseUrl}image/library-cover?libraryId=${libraryId}&${this.coverAuthParams()}`;
  }

  getVolumeCoverImage(volumeId: number) {
    return `${this.baseUrl}image/volume-cover?volumeId=${volumeId}&${this.coverAuthParams()}`;
  }

  getSeriesCoverImage(seriesId: number) {
    return `${this.baseUrl}image/series-cover?seriesId=${seriesId}&${this.coverAuthParams()}`;
  }

  getCollectionCoverImage(collectionTagId: number) {
    return `${this.baseUrl}image/collection-cover?collectionTagId=${collectionTagId}&${this.coverAuthParams()}`;
  }

  getReadingListCoverImage(readingListId: number) {
    return `${this.baseUrl}image/readinglist-cover?readingListId=${readingListId}&${this.coverAuthParams()}`;
  }

  getChapterCoverImage(chapterId: number) {
    return `${this.baseUrl}image/chapter-cover?chapterId=${chapterId}&${this.coverAuthParams()}`;
  }

  getBookmarkedImage(chapterId: number, pageNum: number, imageOffset: number = 0) {
    return `${this.baseUrl}image/bookmark?chapterId=${chapterId}&apiKey=${this.encodedKey()}&pageNum=${pageNum}&imageOffset=${imageOffset}`;
  }

  getWebLinkImage(url: string) {
    return `${this.baseUrl}image/web-link?url=${encodeURIComponent(url)}&apiKey=${this.encodedKey()}`;
  }

  getPublisherImage(name: string) {
    return `${this.baseUrl}image/publisher?publisherName=${encodeURIComponent(name)}&apiKey=${this.encodedKey()}`;
  }

  getCoverUploadImage(filename: string) {
    return `${this.baseUrl}image/cover-upload?filename=${encodeURIComponent(filename)}&apiKey=${this.encodedKey()}`;
  }

  getKavitaPlusSeriesCoverImages(seriesId: number, volumeId: number | null = null, chapterId: number | null = null) {
    const base = (chapterId === null) ? volumeId == null ? 'series' : 'volume' : 'chapter';
    const volStr = volumeId == null ? '' : `&volumeId=${volumeId}`;
    const chStr = chapterId == null ? '' : `&chapterId=${chapterId}`;

    return this.httpClient.get<ExternalCoverResponse[]>(`${this.baseUrl}image/external/${base}?seriesId=${seriesId}${volStr}${chStr}`).pipe(
      map(res => res
        .filter(res => [ExternalCoverImageType.Volume, ExternalCoverImageType.Chapter, ExternalCoverImageType.Series, ExternalCoverImageType.Issue].includes(res.type))
        .map(d => {
          const langSuffix = d.language ? ` (${d.language})` : '';

          let label: string;
          switch (d.type) {
            case ExternalCoverImageType.Series:
              label = d.language ?? '';
              break;
            case ExternalCoverImageType.Volume:
              label = translate('common.volume-num-shorthand', { num: d.number });
              break;
            case ExternalCoverImageType.Chapter:
              label = translate('common.chapter-num-shorthand', { num: d.number });
              break;
            case ExternalCoverImageType.Issue:
              label = translate('common.issue-hash-num', { num: d.number });
              break;
            default:
              label = '';
          }

          // Series uses language as the label itself; others append it as a suffix
          const title = d.type === ExternalCoverImageType.Series
            ? label
            : label + langSuffix;

          return { url: d.url, title } as CoverImageOption;
      })
      )
    );
  }

  /**
   * Used to refresh an existing loaded image (lazysizes). If random already attached, will append another number onto it.
   * @param url Existing request url from ImageService only
   * @returns Url with a random parameter attached
   */
  randomize(url: string) {
    const r = Math.round(Math.random() * 100 + 1);
    if (url.indexOf('&random') >= 0) {
      return url + 1;
    }
    return url + '&random=' + r;
  }
}
