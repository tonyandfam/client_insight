export interface ICompanyFeedQuery {
  days?: number;
  limit?: number;
  activeOnly?: boolean;
  minScore?: number | null;
}

export interface ICompanyClientFeedDto {
  clientId: string; // Guid
  clientName: string;
  clientWebsite?: string | null;
  clientAddress?: string | null;
  companyRank?: number | null;
  articles: ICompanyClientFeedArticleDto[];
}

export interface ICompanyClientFeedArticleDto {
  articleId: number; // long
  articleLanguage?: string | null;
  title?: string | null;
  englishSummary?: string | null;
  relevanceScore?: number | null; // decimal?
  reasonForScore?: string | null;
  conversationAngle?: string | null;
  url: string;
  sourceCountry?: string | null;
  publishedAt?: string | null; // DateTimeOffset? -> ISO string
  socialImageUrl?: string | null;
}
