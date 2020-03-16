﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grand.Core;
using Grand.Core.Caching;
using Grand.Core.Domain.Catalog;
using Grand.Core.Domain.Common;
using Grand.Core.Domain.Vendors;
using Grand.Services.Catalog;
using Grand.Services.Common;
using Grand.Services.Customers;
using Grand.Services.Directory;
using Grand.Services.Localization;
using Grand.Services.Security;
using Grand.Services.Stores;
using Grand.Services.Vendors;
using Grand.Web.Features.Models.Catalog;
using Grand.Web.Infrastructure.Cache;
using Grand.Web.Interfaces;
using Grand.Web.Models.Catalog;
using MediatR;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Grand.Web.Features.Handlers.Catalog
{
    public class GetSearchHandler : IRequestHandler<GetSearch, SearchModel>
    {
        private readonly ICategoryService _categoryService;
        private readonly ICacheManager _cacheManager;
        private readonly IAclService _aclService;
        private readonly IStoreMappingService _storeMappingService;
        private readonly ILocalizationService _localizationService;
        private readonly IManufacturerService _manufacturerService;
        private readonly IVendorService _vendorService;
        private readonly ICurrencyService _currencyService;
        private readonly IProductService _productService;
        private readonly IMediator _mediator;
        private readonly IProductViewModelService _productViewModelService;
        private readonly ISearchTermService _searchTermService;

        private readonly CatalogSettings _catalogSettings;
        private readonly VendorSettings _vendorSettings;

        public GetSearchHandler(
            ICategoryService categoryService, 
            ICacheManager cacheManager, 
            IAclService aclService, 
            IStoreMappingService storeMappingService, 
            ILocalizationService localizationService, 
            IManufacturerService manufacturerService, 
            IVendorService vendorService, 
            ICurrencyService currencyService, 
            IProductService productService, 
            IMediator mediator, 
            IProductViewModelService productViewModelService, 
            ISearchTermService searchTermService, 
            CatalogSettings catalogSettings, 
            VendorSettings vendorSettings)
        {
            _categoryService = categoryService;
            _cacheManager = cacheManager;
            _aclService = aclService;
            _storeMappingService = storeMappingService;
            _localizationService = localizationService;
            _manufacturerService = manufacturerService;
            _vendorService = vendorService;
            _currencyService = currencyService;
            _productService = productService;
            _mediator = mediator;
            _productViewModelService = productViewModelService;
            _searchTermService = searchTermService;
            _catalogSettings = catalogSettings;
            _vendorSettings = vendorSettings;
        }

        public async Task<SearchModel> Handle(GetSearch request, CancellationToken cancellationToken)
        {
            if (request.Model == null)
                request.Model = new SearchModel();

            var searchTerms = request.Model.q;
            if (searchTerms == null)
                searchTerms = "";
            searchTerms = searchTerms.Trim();

            if (request.Model.Box)
                request.Model.sid = _catalogSettings.SearchByDescription;
            if (request.Model.sid)
                request.Model.adv = true;

            //view/sorting/page size
            var options = await _mediator.Send(new GetViewSortSizeOptions() { 
                Command = request.Command,
                PagingFilteringModel = request.Model.PagingFilteringContext,
                Language = request.Language,
                AllowCustomersToSelectPageSize = _catalogSettings.SearchPageAllowCustomersToSelectPageSize,
                PageSizeOptions = _catalogSettings.SearchPagePageSizeOptions,
                PageSize = _catalogSettings.SearchPageProductsPerPage
            });
            request.Model.PagingFilteringContext = options.pagingFilteringModel;
            request.Command = options.command;


            string cacheKey = string.Format(ModelCacheEventConst.SEARCH_CATEGORIES_MODEL_KEY,
               request.Language.Id,
                string.Join(",", request.Customer.GetCustomerRoleIds()),
                request.Store.Id);
            var categories = await _cacheManager.GetAsync(cacheKey, async () =>
            {
                var categoriesModel = new List<SearchModel.CategoryModel>();
                //all categories
                var allCategories = await _categoryService.GetAllCategories(storeId: request.Store.Id);
                foreach (var c in allCategories)
                {
                    //generate full category name (breadcrumb)
                    string categoryBreadcrumb = "";
                    var breadcrumb = c.GetCategoryBreadCrumb(allCategories, _aclService, _storeMappingService);
                    for (int i = 0; i <= breadcrumb.Count - 1; i++)
                    {
                        categoryBreadcrumb += breadcrumb[i].GetLocalized(x => x.Name,request.Language.Id);
                        if (i != breadcrumb.Count - 1)
                            categoryBreadcrumb += " >> ";
                    }
                    categoriesModel.Add(new SearchModel.CategoryModel {
                        Id = c.Id,
                        Breadcrumb = categoryBreadcrumb
                    });
                }
                return categoriesModel;
            });
            if (categories.Any())
            {
                //first empty entry
                request.Model.AvailableCategories.Add(new SelectListItem {
                    Value = "",
                    Text = _localizationService.GetResource("Common.All")
                });
                //all other categories
                foreach (var c in categories)
                {
                    request.Model.AvailableCategories.Add(new SelectListItem {
                        Value = c.Id.ToString(),
                        Text = c.Breadcrumb,
                        Selected = request.Model.cid == c.Id
                    });
                }
            }

            var manufacturers = await _manufacturerService.GetAllManufacturers();
            if (manufacturers.Any())
            {
                request.Model.AvailableManufacturers.Add(new SelectListItem {
                    Value = "",
                    Text = _localizationService.GetResource("Common.All")
                });
                foreach (var m in manufacturers)
                    request.Model.AvailableManufacturers.Add(new SelectListItem {
                        Value = m.Id.ToString(),
                        Text = m.GetLocalized(x => x.Name,request.Language.Id),
                        Selected = request.Model.mid == m.Id
                    });
            }

            request.Model.asv = _vendorSettings.AllowSearchByVendor;
            if (request.Model.asv)
            {
                var vendors = await _vendorService.GetAllVendors();
                if (vendors.Any())
                {
                    request.Model.AvailableVendors.Add(new SelectListItem {
                        Value = "",
                        Text = _localizationService.GetResource("Common.All")
                    });
                    foreach (var vendor in vendors)
                        request.Model.AvailableVendors.Add(new SelectListItem {
                            Value = vendor.Id.ToString(),
                            Text = vendor.GetLocalized(x => x.Name,request.Language.Id),
                            Selected = request.Model.vid == vendor.Id
                        });
                }
            }

            IPagedList<Product> products = new PagedList<Product>(new List<Product>(), 0, 1);

            if (request.IsSearchTermSpecified)
            {
                if (searchTerms.Length < _catalogSettings.ProductSearchTermMinimumLength)
                {
                    request.Model.Warning = string.Format(_localizationService.GetResource("Search.SearchTermMinimumLengthIsNCharacters"), _catalogSettings.ProductSearchTermMinimumLength);
                }
                else
                {
                    var categoryIds = new List<string>();
                    string manufacturerId = "";
                    decimal? minPriceConverted = null;
                    decimal? maxPriceConverted = null;
                    bool searchInDescriptions = false;
                    string vendorId = "";
                    if (request.Model.adv)
                    {
                        //advanced search
                        var categoryId = request.Model.cid;
                        if (!String.IsNullOrEmpty(categoryId))
                        {
                            categoryIds.Add(categoryId);
                            if (request.Model.isc)
                            {
                                //include subcategories
                                categoryIds.AddRange(await _mediator.Send(new GetChildCategoryIds() { ParentCategoryId = categoryId, Customer = request.Customer, Store = request.Store }));

                            }
                        }
                        manufacturerId = request.Model.mid;

                        //min price
                        if (!string.IsNullOrEmpty(request.Model.pf))
                        {
                            decimal minPrice;
                            if (decimal.TryParse(request.Model.pf, out minPrice))
                                minPriceConverted = await _currencyService.ConvertToPrimaryStoreCurrency(minPrice, request.Currency);
                        }
                        //max price
                        if (!string.IsNullOrEmpty(request.Model.pt))
                        {
                            decimal maxPrice;
                            if (decimal.TryParse(request.Model.pt, out maxPrice))
                                maxPriceConverted = await _currencyService.ConvertToPrimaryStoreCurrency(maxPrice, request.Currency);
                        }

                        searchInDescriptions = request.Model.sid;
                        if (request.Model.asv)
                            vendorId = request.Model.vid;
                    }

                    var searchInProductTags = searchInDescriptions;

                    //products
                    products = (await _productService.SearchProducts(
                        categoryIds: categoryIds,
                        manufacturerId: manufacturerId,
                        storeId: request.Store.Id,
                        visibleIndividuallyOnly: true,
                        priceMin: minPriceConverted,
                        priceMax: maxPriceConverted,
                        keywords: searchTerms,
                        searchDescriptions: searchInDescriptions,
                        searchSku: searchInDescriptions,
                        searchProductTags: searchInProductTags,
                        languageId:request.Language.Id,
                        orderBy: (ProductSortingEnum)request.Command.OrderBy,
                        pageIndex: request.Command.PageNumber - 1,
                        pageSize: request.Command.PageSize,
                        vendorId: vendorId)).products;
                    request.Model.Products = (await _productViewModelService.PrepareProductOverviewModels(products, prepareSpecificationAttributes: _catalogSettings.ShowSpecAttributeOnCatalogPages)).ToList();

                    request.Model.NoResults = !request.Model.Products.Any();

                    //search term statistics
                    if (!String.IsNullOrEmpty(searchTerms))
                    {
                        var searchTerm = await _searchTermService.GetSearchTermByKeyword(searchTerms, request.Store.Id);
                        if (searchTerm != null)
                        {
                            searchTerm.Count++;
                            await _searchTermService.UpdateSearchTerm(searchTerm);
                        }
                        else
                        {
                            searchTerm = new SearchTerm {
                                Keyword = searchTerms,
                                StoreId = request.Store.Id,
                                Count = 1
                            };
                            await _searchTermService.InsertSearchTerm(searchTerm);
                        }
                    }
                }
            }

            request.Model.PagingFilteringContext.LoadPagedList(products);

            return request.Model;
        }
    }
}
