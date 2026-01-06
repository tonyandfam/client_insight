export interface ICompanyListItemDto {
  companyId: string; // Guid
  nickname?: string | null;
  importedAt: string; // DateTime -> ISO string
  activeClientCount: number;
  totalClientCount: number;
}
