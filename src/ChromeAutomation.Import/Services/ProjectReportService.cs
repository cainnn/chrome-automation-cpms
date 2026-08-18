using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace PersonalPMS.ProjectReport.Services
{
    public class ProjectReportService
    {
        private readonly PersonalPMSModel.PersonalPMSModel _context;
        private readonly ILogger<ProjectReportService> _logger;

        public ProjectReportService(PersonalPMSModel.PersonalPMSModel context, ILogger<ProjectReportService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<int> GetProjectCountAsync()
        {
            try
            {
                return await _context.PMS项目明细报表s.CountAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting project count");
                throw;
            }
        }

        public async Task<List<PersonalPMSModel.PMS项目明细报表>> GetProjectsAsync(int take = 10, int skip = 0)
        {
            try
            {
                return await _context.PMS项目明细报表s
                    .Skip(skip)
                    .Take(take)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting projects");
                throw;
            }
        }

        public async Task<List<PersonalPMSModel.PMS项目明细报表>> GetProjectsByStatusAsync(string status)
        {
            try
            {
                return await _context.PMS项目明细报表s
                    .Where(p => p.项目状态 == status)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting projects by status: {Status}", status);
                throw;
            }
        }

        public async Task<List<PersonalPMSModel.PMS项目明细报表>> GetProjectsByYearAsync(int year)
        {
            try
            {
                return await _context.PMS项目明细报表s
                    .Where(p => p.项目年份 == year)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting projects by year: {Year}", year);
                throw;
            }
        }
    }
} 